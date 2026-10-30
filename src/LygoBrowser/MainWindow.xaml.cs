using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LygoBrowser;

public partial class MainWindow : Window, IBrowserHost
{
    private sealed class TabEntry
    {
        public int Id;
        public WebView2 View;
        public Border Ui;
        public TextBlock TitleText;
        public Image Favicon;
        public string Url = "";
        public string Title = "New tab";
        public bool Loaded;
    }

    private sealed class SuggestItem
    {
        public string Title { get; init; } = "";
        public string Url { get; init; } = "";
        public string Kind { get; init; } = "";
        public override string ToString() =>
            Kind == "search" ? $"Search the web for \u201c{Title}\u201d"
                             : $"{Title}   \u00b7   {Bookmark.DomainOf(Url)}   [{Kind}]";
    }

    private readonly List<TabEntry> _tabs = new();
    private readonly Stack<(string Url, string Title)> _closed = new();
    private readonly List<SuggestItem> _suggest = new();
    private CoreWebView2Environment _env;
    private LygoService _service;
    private TabEntry _active;
    private int _nextId = 1;
    private string _home;
    private readonly string _startupUrl;
    private string _engine = "WebView2";
    private DispatcherTimer _flashTimer;
    private DispatcherTimer _linkTimer;
    private Settings _settings = new();
    private bool _suppressSuggest;
    private bool _findOpen;

    public MainWindow(string startupUrl = null)
    {
        InitializeComponent();
        _startupUrl = string.IsNullOrWhiteSpace(startupUrl) ? null : startupUrl;
        _home = $"http://127.0.0.1:{Paths.BridgePort}/";
        BuildShortcuts();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    /// <summary>Keyboard bindings as data, so the key handler stays a lookup instead of a switch.</summary>
    private void BuildShortcuts()
    {
        _shortcuts = new Dictionary<(Key, bool, bool), Action>
        {
            [(Key.T, true, false)] = () => _ = CreateTabAsync(_settings.Homepage, true),
            [(Key.T, true, true)] = ReopenClosed,
            [(Key.W, true, false)] = () => { if (_active != null) CloseTab(_active); },
            [(Key.L, true, false)] = () => { AddressBox.Focus(); AddressBox.SelectAll(); },
            [(Key.D, true, false)] = BookmarkCurrent,
            [(Key.F, true, false)] = OpenFind,
            [(Key.H, true, false)] = () => Navigate(_home + "history"),
            [(Key.J, true, false)] = () => Navigate(_home + "downloads"),
            [(Key.P, true, false)] = DoPrint,
            [(Key.U, true, false)] = ViewSource,
            [(Key.S, true, true)] = () => _ = SnapAsync("viewport", null),
            [(Key.F5, false, false)] = () => ReloadBtn_Click(this, new RoutedEventArgs()),
            [(Key.F3, false, false)] = () => RunFind(true),
            [(Key.F3, false, true)] = () => RunFind(false),
            [(Key.F12, false, false)] = () => MenuDevTools_Click(this, new RoutedEventArgs()),
            [(Key.OemPlus, true, false)] = () => Zoom(0.1),
            [(Key.Add, true, false)] = () => Zoom(0.1),
            [(Key.OemMinus, true, false)] = () => Zoom(-0.1),
            [(Key.Subtract, true, false)] = () => Zoom(-0.1),
            [(Key.D0, true, false)] = ZoomReset,
            [(Key.NumPad0, true, false)] = ZoomReset,
        };
    }

    // ------------------------------------------------------------------ startup

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Paths.EnsureDirs();
            _settings = SettingsStore.Load();
            if (string.IsNullOrWhiteSpace(_settings.Homepage)) _settings.Homepage = _home;
            StartService();
            BuildBookmarkBar();

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    $"--remote-debugging-port={Paths.CdpPort} --disable-features=msWebOOUI,msPdfOOUI"
            };
            _env = await CoreWebView2Environment.CreateAsync(null, Paths.ProfileDir, options);
            _engine = _env.BrowserVersionString;
            Log.Info($"webview2 {_engine} cdp {Paths.CdpPort}");
            CdpPillText.Text = "CDP " + Paths.CdpPort;

            if (_startupUrl != null)
            {
                // Launched with a URL (e.g. the console handing us its UI): that wins over the session.
                await CreateTabAsync(_startupUrl, true);
            }
            else
            {
                var restored = await RestoreSessionAsync();
                if (!restored) await CreateTabAsync(_settings.Homepage, true);
            }
            UpdateCounts();
            UpdateZoomLabel();
            StartLinkTimer();
        }
        catch (Exception ex)
        {
            Log.Error("startup: " + ex);
            MessageBox.Show("LYGO Browser could not start the engine:\n\n" + ex.Message,
                            "LYGO BROWSER", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartService()
    {
        try
        {
            _service = new LygoService(this);
            _service.Start();
            BridgeDot.Fill = (Brush)FindResource("Mint");
            BridgePillText.Text = "BRIDGE " + Paths.BridgePort;
            SetStatus("BRIDGE ONLINE · " + _home);
        }
        catch (Exception ex)
        {
            BridgeDot.Fill = (Brush)FindResource("Danger");
            BridgePillText.Text = "BRIDGE DOWN";
            Log.Error("service: " + ex.Message);
            SetStatus("BRIDGE FAILED: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ tabs

    private async Task<TabEntry> CreateTabAsync(string url, bool activate)
    {
        var entry = new TabEntry { Id = _nextId++ };
        entry.View = new WebView2 { Visibility = Visibility.Collapsed, Margin = new Thickness(0) };
        TabHost.Children.Add(entry.View);
        _tabs.Add(entry);

        BuildTabHeader(entry);
        await entry.View.EnsureCoreWebView2Async(_env);
        entry.Loaded = false;
        WireTabEvents(entry);

        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) uri = new Uri("https://" + url);
            entry.View.Source = uri;
        }
        if (activate) Activate(entry);
        return entry;
    }

    private void WireTabEvents(TabEntry entry)
    {
        var core = entry.View.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;   // we own the shortcuts
        core.Settings.IsPasswordAutosaveEnabled = false;          // the LYGO vault owns secrets
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;

        core.NewWindowRequested += (_, ev) => OpenAuthPopup(ev);
        core.DocumentTitleChanged += (_, _) =>
        {
            entry.Title = core.DocumentTitle;
            entry.TitleText.Text = Shorten(entry.Title, 24);
        };
        core.SourceChanged += (_, _) => OnTabSourceChanged(entry, core);
        core.FaviconChanged += async (_, _) => await LoadFaviconAsync(entry);
        core.NavigationStarting += (_, _) => OnTabNavigationStarting(entry, core);
        core.NavigationCompleted += (_, ev) => OnTabNavigationCompleted(entry, core, ev);
        core.DownloadStarting += (_, ev) => BeginDownload(ev);
        core.PermissionRequested += (_, ev) => AskPermission(ev);
        core.ContextMenuRequested += (_, ev) => ExtendContextMenu(ev);
    }

    /// <summary>
    /// OAuth popups (Google, Discord, Patreon) must be real popup windows: window.opener has to
    /// survive so the provider can post the result back. Forcing them into a tab breaks sign-in.
    /// </summary>
    private async void OpenAuthPopup(CoreWebView2NewWindowRequestedEventArgs ev)
    {
        var deferral = ev.GetDeferral();
        try
        {
            if (string.IsNullOrWhiteSpace(ev.Uri)) { ev.Handled = true; return; }
            var popup = new WebView2();
            var window = new Window
            {
                Title = "LYGO Browser \u00b7 sign in",
                Width = 520,
                Height = 720,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (Brush)FindResource("Obsidian"),
                Content = popup
            };
            window.Show();
            await popup.EnsureCoreWebView2Async(_env);
            ev.NewWindow = popup.CoreWebView2;   // preserves the opener relationship
            Log.Info("auth popup: " + ev.Uri[..Math.Min(80, ev.Uri.Length)]);
        }
        catch (Exception ex)
        {
            Log.Error("auth popup: " + ex.Message);
        }
        finally
        {
            ev.Handled = true;
            deferral.Complete();
        }
    }

    private void OnTabSourceChanged(TabEntry entry, CoreWebView2 core)
    {
        entry.Url = core.Source ?? "";
        if (!ReferenceEquals(entry, _active)) return;
        SetAddress(entry.Url);
        ApplySecurityGlyph(entry.Url);
    }

    private void OnTabNavigationStarting(TabEntry entry, CoreWebView2 core)
    {
        entry.Loaded = false;
        if (ReferenceEquals(entry, _active))
            SetStatus("LOADING " + Shorten(core.Source ?? "", 90));
    }

    private void OnTabNavigationCompleted(TabEntry entry, CoreWebView2 core,
                                          CoreWebView2NavigationCompletedEventArgs ev)
    {
        entry.Loaded = true;
        var active = ReferenceEquals(entry, _active);
        if (!ev.IsSuccess && active) SetStatus("LOAD FAILED · " + ev.WebErrorStatus);
        else if (active) SetStatus("DONE");
        if (ev.IsSuccess)
        {
            entry.Url = core.Source ?? entry.Url;
            entry.Title = core.DocumentTitle;
            HistoryStore.Add(entry.Url, entry.Title);
            _ = LoadFaviconAsync(entry);                     // in case FaviconChanged never fires
            if (entry.Url.Contains("127.0.0.1:9337"))
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (entry.Favicon.Visibility != Visibility.Visible) UseBrandFavicon(entry);
                }), DispatcherPriority.Background);
        }
        if (active) RefreshNavButtons();
    }

    private void BuildTabHeader(TabEntry entry)
    {
        var favicon = new Image
        {
            Width = 14, Height = 14, Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed
        };
        var title = new TextBlock
        {
            Text = "New tab", FontSize = 11.5, Foreground = (Brush)FindResource("Muted"),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 170
        };
        var close = new Button
        {
            Content = "\u2715", FontFamily = new FontFamily("Segoe UI"), FontSize = 9,
            Width = 18, Height = 18, Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("ChromeButton"), VerticalAlignment = VerticalAlignment.Center
        };
        close.Click += (_, _) => CloseTab(entry);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(favicon);
        panel.Children.Add(title);
        panel.Children.Add(close);

        var ui = new Border
        {
            Height = 34, Padding = new Thickness(10, 0, 8, 0), Margin = new Thickness(0, 2, 2, 0),
            CornerRadius = new CornerRadius(4, 4, 0, 0), Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = panel, Cursor = Cursors.Hand
        };
        ui.MouseLeftButtonDown += (_, _) => Activate(entry);
        TabStrip.Children.Add(ui);
        entry.Ui = ui;
        entry.TitleText = title;
        entry.Favicon = favicon;
    }

    private async Task LoadFaviconAsync(TabEntry entry)
    {
        try
        {
            var core = entry.View.CoreWebView2;
            if (core == null) return;
            using var raw = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (raw == null)
            {
                UseBrandFavicon(entry);
                return;
            }
            // The stream arrives at an unknown position; copy it so the decoder starts at 0.
            using var buffer = new MemoryStream();
            await raw.CopyToAsync(buffer);
            buffer.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = buffer;
            bmp.DecodePixelWidth = 14;
            bmp.EndInit();
            bmp.Freeze();
            entry.Favicon.Source = bmp;
            entry.Favicon.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Warn("favicon: " + ex.Message);
            UseBrandFavicon(entry);
        }
    }

    private void UseBrandFavicon(TabEntry entry)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "brand", "lygo-browser-96.png");
            if (!File.Exists(path)) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 14;
            bmp.EndInit();
            bmp.Freeze();
            entry.Favicon.Source = bmp;
            entry.Favicon.Visibility = Visibility.Visible;
        }
        catch { /* cosmetic only */ }
    }

    private void Activate(TabEntry entry)
    {
        if (entry == null) return;
        _active = entry;
        foreach (var t in _tabs)
        {
            var isActive = ReferenceEquals(t, entry);
            t.View.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            t.Ui.Background = isActive ? (Brush)FindResource("Panel2") : Brushes.Transparent;
            t.Ui.BorderBrush = isActive ? (Brush)FindResource("Gold") : Brushes.Transparent;
            t.TitleText.Foreground = isActive ? (Brush)FindResource("Text") : (Brush)FindResource("Muted");
        }
        SetAddress(entry.Url);
        ApplySecurityGlyph(entry.Url);
        RefreshNavButtons();
        UpdateZoomLabel();
        entry.View.Focus();
    }

    private void SetAddress(string url)
    {
        _suppressSuggest = true;
        AddressBox.Text = url ?? "";
        _suppressSuggest = false;
        AddressHint.Visibility = string.IsNullOrEmpty(AddressBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        AddressBox.CaretIndex = AddressBox.Text.Length;
    }

    private void Navigate(string url)
    {
        if (_active == null) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) url = "https://" + url;
        _active.View.Source = new Uri(url);
    }

    private void CloseTab(TabEntry entry)
    {
        if (_tabs.Count == 1)
        {
            Navigate(_settings.Homepage);
            return;
        }
        var wasActive = ReferenceEquals(entry, _active);
        if (!string.IsNullOrWhiteSpace(entry.Url) && !entry.Url.Contains("127.0.0.1:9337"))
        {
            _closed.Push((entry.Url, entry.Title));
            while (_closed.Count > 12) _closed.TrimExcess();
        }
        TabHost.Children.Remove(entry.View);
        TabStrip.Children.Remove(entry.Ui);
        _tabs.Remove(entry);
        entry.View.Dispose();
        if (wasActive) Activate(_tabs[^1]);
    }

    // ------------------------------------------------------------------ session

    private sealed class SessionDoc
    {
        public List<SessionTab> Tabs { get; set; } = new();
        public int Active { get; set; }
    }

    private sealed class SessionTab
    {
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
    }

    private static string SessionFile => Path.Combine(Paths.StateDir, "session.json");

    private async Task<bool> RestoreSessionAsync()
    {
        if (!_settings.RestoreSession || !File.Exists(SessionFile)) return false;
        try
        {
            var doc = JsonSerializer.Deserialize<SessionDoc>(File.ReadAllText(SessionFile));
            var urls = doc?.Tabs?.Select(t => t.Url).Where(u => !string.IsNullOrWhiteSpace(u))
                          .Take(12).ToList() ?? new List<string>();
            if (urls.Count == 0) return false;
            foreach (var url in urls) await CreateTabAsync(url, false);
            var index = Math.Clamp(doc.Active, 0, _tabs.Count - 1);
            Activate(_tabs[index]);
            SetStatus($"RESTORED {urls.Count} TAB(S)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("session restore: " + ex.Message);
            return false;
        }
    }

    private void SaveSession()
    {
        if (!_settings.RestoreSession) return;
        try
        {
            var doc = new SessionDoc
            {
                Tabs = _tabs.Where(t => !string.IsNullOrWhiteSpace(t.Url))
                            .Select(t => new SessionTab { Url = t.Url, Title = t.Title }).ToList(),
                Active = Math.Max(0, _tabs.IndexOf(_active))
            };
            Directory.CreateDirectory(Paths.StateDir);
            File.WriteAllText(SessionFile, JsonSerializer.Serialize(doc), new UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Warn("session save: " + ex.Message); }
    }

    private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSession();
        _service?.Dispose();
    }

    // ------------------------------------------------------------------ security glyph

    private void ApplySecurityGlyph(string url)
    {
        if (string.IsNullOrEmpty(url)) { LockGlyph.Text = "\uE72E"; return; }
        if (url.StartsWith("https://"))
        {
            LockGlyph.Text = "\uE72E";
            LockGlyph.Foreground = (Brush)FindResource("Mint");
            LockGlyph.ToolTip = "Secure connection (TLS)";
        }
        else if (url.StartsWith("http://127.0.0.1") || url.StartsWith("http://localhost"))
        {
            LockGlyph.Text = "\uE72E";
            LockGlyph.Foreground = (Brush)FindResource("Gold");
            LockGlyph.ToolTip = "Local LYGO Browser page";
        }
        else
        {
            LockGlyph.Text = "\uE785";
            LockGlyph.Foreground = (Brush)FindResource("Danger");
            LockGlyph.ToolTip = "Not secure";
        }
    }

    // ------------------------------------------------------------------ address bar

    private void AddressBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressHint.Visibility = Visibility.Collapsed;
        AddressBox.SelectAll();
    }

    private void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressHint.Visibility = string.IsNullOrEmpty(AddressBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        t.Tick += (_, _) => { t.Stop(); SuggestPopup.IsOpen = false; };
        t.Start();
    }

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSuggestions();

    private void UpdateSuggestions()
    {
        if (_suppressSuggest) return;
        var text = AddressBox.Text.Trim();
        AddressHint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _suggest.Clear();
        SuggestPopup.IsOpen = false;
        if (text.Length < 2) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, rec) in BibleStore.Search(text, 6))
        {
            if (!seen.Add(rec.Url)) continue;
            _suggest.Add(new SuggestItem { Title = rec.Title, Url = rec.Url, Kind = "bible" });
        }
        foreach (var h in HistoryStore.Search(text, 4))
        {
            if (!seen.Add(h.Url)) continue;
            _suggest.Add(new SuggestItem { Title = string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title, Url = h.Url, Kind = "history" });
        }
        if (!LooksLikeUrl(text))
            _suggest.Add(new SuggestItem { Title = text, Url = text, Kind = "search" });

        if (_suggest.Count == 0) return;
        SuggestList.ItemsSource = null;
        SuggestList.ItemsSource = _suggest;
        SuggestList.SelectedIndex = 0;
        SuggestPopup.Width = Math.Max(320, AddressArea.ActualWidth);
        SuggestPopup.IsOpen = true;
    }

    private void SuggestList_Click(object sender, MouseButtonEventArgs e) => AcceptSuggestion();

    private void SuggestList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AcceptSuggestion(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SuggestPopup.IsOpen = false; AddressBox.Focus(); e.Handled = true; }
    }

    private void AcceptSuggestion()
    {
        if (SuggestList.SelectedItem is not SuggestItem item) return;
        SuggestPopup.IsOpen = false;
        SetAddress("");
        if (item.Kind == "search")
        {
            var hits = BibleStore.Search(item.Title, 1);
            Navigate(hits.Count > 0 && _settings.SearchEngine != "bible"
                ? _home + "bible?q=" + Uri.EscapeDataString(item.Title)
                : hits.Count > 0 && _settings.SearchEngine == "bible"
                    ? _home + "bible?q=" + Uri.EscapeDataString(item.Title)
                    : _settings.SearchUrl(item.Title));
        }
        else
        {
            Navigate(item.Url);
        }
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (SuggestPopup.IsOpen)
        {
            if (e.Key == Key.Down)
            {
                SuggestList.SelectedIndex = Math.Min(SuggestList.SelectedIndex + 1, _suggest.Count - 1);
                e.Handled = true; return;
            }
            if (e.Key == Key.Up)
            {
                SuggestList.SelectedIndex = Math.Max(SuggestList.SelectedIndex - 1, 0);
                e.Handled = true; return;
            }
            if (e.Key == Key.Enter && SuggestList.SelectedItem != null)
            {
                AcceptSuggestion(); e.Handled = true; return;
            }
        }
        if (e.Key == Key.Enter) { Go(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            SuggestPopup.IsOpen = false;
            SetAddress(_active?.Url ?? "");
        }
    }

    private void GoBtn_Click(object sender, RoutedEventArgs e) => Go();

    private void Go()
    {
        var text = AddressBox.Text.Trim();
        if (text.Length == 0) return;
        SuggestPopup.IsOpen = false;
        if (LooksLikeUrl(text))
        {
            Navigate(text.Contains("://") ? text : "https://" + text);
            return;
        }
        var hits = BibleStore.Search(text, 1);
        Navigate(hits.Count > 0
            ? _home + "bible?q=" + Uri.EscapeDataString(text)
            : _settings.SearchUrl(text));
    }

    private static bool LooksLikeUrl(string s)
    {
        if (s.Contains(' ')) return false;
        if (s.Contains("://")) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(
            s, @"^[\w.\-]+\.[a-z]{2,}([:/?#].*)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // ------------------------------------------------------------------ find in page

    private void OpenFind()
    {
        if (_active?.View.CoreWebView2 == null) return;
        FindBar.Visibility = Visibility.Visible;
        _findOpen = true;
        FindBox.Text = "";
        FindCount.Text = "0 / 0";
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FindBox.Focus();
            Keyboard.Focus(FindBox);
            FindBox.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void CloseFind()
    {
        _findOpen = false;
        FindBar.Visibility = Visibility.Collapsed;
        try { _active?.View.CoreWebView2?.Find.Stop(); } catch { }
        _active?.View.Focus();
    }

    private async void RunFind(bool forward)
    {
        var core = _active?.View.CoreWebView2;
        if (core == null || !_findOpen) return;
        var term = FindBox.Text;
        if (term.Length == 0) { try { core.Find.Stop(); } catch { } FindCount.Text = "0 / 0"; return; }
        try
        {
            var find = core.Find;
            if (find.MatchCount == 0 || FindCount.Tag as string != term)
            {
                var options = _env.CreateFindOptions();
                options.FindTerm = term;
                options.ShouldHighlightAllMatches = true;
                options.SuppressDefaultFindDialog = true;
                await find.StartAsync(options);
                FindCount.Tag = term;
            }
            else if (forward) find.FindNext();
            else find.FindPrevious();
            ShowFindCount(find);
        }
        catch (Exception ex) { Log.Warn("find: " + ex.Message); }
    }

    private void ShowFindCount(CoreWebView2Find find)
    {
        var total = find.MatchCount;
        var index = total > 0 ? find.ActiveMatchIndex + 1 : 0;
        FindCount.Text = $"{index} / {total}";
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunFind((Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
        else if (e.Key == Key.F3) { RunFind(true); e.Handled = true; }
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FindCount.Tag = null;
        RunFind(true);
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => RunFind(true);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => RunFind(false);
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    // ------------------------------------------------------------------ zoom

    private void Zoom(double delta)
    {
        if (_active?.View == null) return;
        _active.View.ZoomFactor = Math.Clamp(_active.View.ZoomFactor + delta, 0.25, 5.0);
        UpdateZoomLabel();
    }

    private void ZoomReset()
    {
        if (_active?.View == null) return;
        _active.View.ZoomFactor = 1.0;
        UpdateZoomLabel();
    }

    private void UpdateZoomLabel()
    {
        if (ZoomText == null) return;
        ZoomText.Text = _active?.View == null
            ? "100%"
            : $"{Math.Round(_active.View.ZoomFactor * 100)}%";
    }

    // ------------------------------------------------------------------ print / save / shot

    private void DoPrint()
    {
        try { _active?.View.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); }
        catch (Exception ex) { Flash("print failed: " + ex.Message); }
    }

    private string OutDir(string kind)
    {
        var dir = Path.Combine(_settings.ResolvedDownloadDir, kind);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SafeName(string title, string ext)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string((title ?? "page").Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim();
        if (clean.Length > 70) clean = clean[..70];
        if (clean.Length == 0) clean = "page";
        return $"{clean}-{DateTime.Now:yyyyMMdd-HHmmss}.{ext}";
    }

    private async Task<string> SavePdf()
    {
        var core = _active?.View.CoreWebView2;
        if (core == null) return null;
        try
        {
            var settings = _env.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = true;
            settings.HeaderTitle = _active.Title;
            settings.FooterUri = core.Source;
            var path = Path.Combine(OutDir("PDF"), SafeName(_active.Title, "pdf"));
            var ok = await core.PrintToPdfAsync(path, settings);
            if (ok) { Flash("PDF SAVED · " + path); return path; }
            Flash("PDF export failed");
            return null;
        }
        catch (Exception ex) { Flash("PDF failed: " + ex.Message); return null; }
    }

    private async Task<string> SavePageShot()
    {
        var result = await SnapAsync("full", null);
        return result is null ? null : "vault";
    }

    /// <summary>Cookies are Chromium's; this exposes the count and a full clear.</summary>
    public Task<int> CookieCountAsync() => UiAsync(async () =>
    {
        var core = _active?.View.CoreWebView2;
        if (core?.CookieManager == null) return 0;
        var cookies = await core.CookieManager.GetCookiesAsync(null);
        return cookies?.Count ?? 0;
    });

    public Task<bool> ClearCookiesAsync() => UiAsync(async () =>
    {
        var core = _active?.View.CoreWebView2;
        if (core?.CookieManager == null) return false;
        // DeleteCookies(null/"" ) throws on this runtime, so enumerate and delete each cookie.
        var cookies = await core.CookieManager.GetCookiesAsync(null);
        foreach (var cookie in cookies ?? new List<Microsoft.Web.WebView2.Core.CoreWebView2Cookie>())
            core.CookieManager.DeleteCookie(cookie);
        Log.Info("cookies cleared (all)");
        return true;
    });

    /// <summary>Rebuild the bookmark bar from the Bible (called after any pin change).</summary>
    public void RefreshBookmarks()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RefreshBookmarks); return; }
        BuildBookmarkBar();
        UpdateCounts();
    }

    /// <summary>Capture the tab into the Shot Vault (viewport or full page).</summary>
    public Task<object> SnapAsync(string mode, int? tabId) => UiAsync(async () =>
    {
        var t = ById(tabId);
        var core = t?.View.CoreWebView2;
        if (core == null) return (object)new { ok = false, error = "no tab" };
        var full = string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase);
        var png = full
            ? await ArchiveService.CaptureFullPageAsync(core)
            : await ArchiveService.CaptureViewportAsync(core);
        if (png == null || png.Length == 0) return (object)new { ok = false, error = "capture failed" };
        var entry = SnapshotStore.Add(core.Source ?? t.Url, core.DocumentTitle ?? t.Title, png,
                                      full ? "full" : "viewport");
        Flash($"SNAP · {entry.W}×{entry.H} · {Path.GetFileName(entry.File)}");
        return (object)new { ok = true, entry };
    });

    private void SnapBtn_Click(object sender, RoutedEventArgs e)
    {
        var full = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        _ = SnapAsync(full ? "full" : "viewport", null);
    }

    private async void SavePageAs()
    {
        try { await _active.View.CoreWebView2.ShowSaveAsUIAsync(); }
        catch (Exception ex) { Flash("save as failed: " + ex.Message); }
    }

    private async void ViewSource()
    {
        var core = _active?.View.CoreWebView2;
        if (core == null) return;
        var html = await core.ExecuteScriptAsync("document.documentElement.outerHTML");
        var text = JsonSerializer.Deserialize<string>(html) ?? "";
        Directory.CreateDirectory(Path.Combine(Paths.Data, "screenshots"));
        var path = Path.Combine(Paths.Data, "screenshots", SafeName("source-" + _active.Title, "html"));
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        await CreateTabAsync("file:///" + path.Replace('\\', '/'), true);
    }

    // ------------------------------------------------------------------ downloads

    private void BeginDownload(CoreWebView2DownloadStartingEventArgs ev)
    {
        try
        {
            var suggested = Path.GetFileName(ev.ResultFilePath);
            if (string.IsNullOrWhiteSpace(suggested)) suggested = "download.bin";
            var dir = _settings.ResolvedDownloadDir;
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, suggested);
            if (File.Exists(target))
            {
                var stem = Path.GetFileNameWithoutExtension(suggested);
                var ext = Path.GetExtension(suggested);
                target = Path.Combine(dir, $"{stem}-{DateTime.Now:HHmmss}{ext}");
            }
            ev.ResultFilePath = target;
            // Deliberately NOT setting Handled: taking ownership of the download kills the app
            // when a CDP client (Playwright) is also intercepting downloads. We still pin the
            // destination and track the operation.

            var entry = new DownloadEntry
            {
                Id = "dl_" + Guid.NewGuid().ToString("N")[..10],
                Url = ev.DownloadOperation.Uri,
                Path = target,
                Total = (long)(ev.DownloadOperation.TotalBytesToReceive ?? 0),
                State = "in-progress",
                Started = BibleStore.NowIso()
            };
            DownloadStore.Track(entry);
            Flash("DOWNLOADING · " + Path.GetFileName(target));
            UpdateDownloadsLabel();

            var op = ev.DownloadOperation;
            op.BytesReceivedChanged += (_, _) =>
            {
                try
                {
                    entry.Bytes = (long)op.BytesReceived;
                    entry.Total = (long)(op.TotalBytesToReceive ?? 0);
                    DownloadStore.Update(entry);
                    UpdateDownloadsLabel();
                }
                catch (Exception ex) { Log.Warn("download progress: " + ex.Message); }
            };
            op.StateChanged += (_, _) => OnDownloadStateChanged(entry, op, target);
        }
        catch (Exception ex)
        {
            Log.Error("download: " + ex.Message);
        }
    }

    private void OnDownloadStateChanged(DownloadEntry entry,
                                        CoreWebView2DownloadOperation op, string target)
    {
        try { TrackDownloadState(entry, op, target); }
        catch (Exception ex) { Log.Warn("download state: " + ex.Message); }
    }

    private void TrackDownloadState(DownloadEntry entry, CoreWebView2DownloadOperation op, string target)
    {
        entry.State = op.State.ToString().ToLowerInvariant();
        // fast downloads never raise BytesReceivedChanged; take the truth from disk
        try
        {
            if (File.Exists(target))
            {
                var size = new FileInfo(target).Length;
                entry.Bytes = size;
                if (entry.Total <= 0) entry.Total = size;
            }
        }
        catch { }
        DownloadStore.Update(entry);
        UpdateDownloadsLabel();
        Flash(entry.State == "completed"
            ? "DOWNLOADED · " + Path.GetFileName(target)
            : "DOWNLOAD " + entry.State.ToUpperInvariant());
    }

    private void UpdateDownloadsLabel()
    {
        if (DownloadText == null) return;
        var active = DownloadStore.Active();
        var all = DownloadStore.All().Count;
        DownloadText.Text = active > 0 ? $"DL {active}" : (all > 0 ? $"DL {all}" : "");
    }

    // ------------------------------------------------------------------ permissions

    private void AskPermission(CoreWebView2PermissionRequestedEventArgs ev)
    {
        try
        {
            var kind = ev.PermissionKind.ToString();
            var host = Bookmark.DomainOf(ev.Uri);
            var answer = MessageBox.Show(
                $"{host} is asking for:\n\n    {kind}\n\nAllow this site to use it?",
                "LYGO BROWSER · permission", MessageBoxButton.YesNo, MessageBoxImage.Question);
            ev.State = answer == MessageBoxResult.Yes
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
            ev.Handled = true;
            Log.Info($"permission {kind} {host} -> {answer}");
        }
        catch (Exception ex) { Log.Warn("permission: " + ex.Message); }
    }

    private void ExtendContextMenu(CoreWebView2ContextMenuRequestedEventArgs ev)
    {
        try
        {
            var items = ev.MenuItems;
            var insertAt = items.Count;
            var archive = _env.CreateContextMenuItem("Archive this page to the Bookmark Bible",
                null, CoreWebView2ContextMenuItemKind.Command);
            archive.CustomItemSelected += async (_, _) =>
            {
                var url = _active?.Url ?? "";
                if (string.IsNullOrWhiteSpace(url)) return;
                var res = BibleStore.Add(url, _active?.Title ?? url, "", Array.Empty<string>(), "", "human");
                await _active.View.CoreWebView2.ExecuteScriptAsync(
                    "document.title='ARCHIVING…'");
                _ = ArchiveRecordAsync(res.Record.Id);
            };
            var addBible = _env.CreateContextMenuItem("Add to Bible (no archive)",
                null, CoreWebView2ContextMenuItemKind.Command);
            addBible.CustomItemSelected += (_, _) =>
            {
                var url = _active?.Url ?? "";
                if (string.IsNullOrWhiteSpace(url)) return;
                var res = BibleStore.Add(url, _active?.Title ?? url, "", Array.Empty<string>(), "", "human");
                Flash($"BIBLE · {res.Status} · {res.Record.Folder}");
                UpdateCounts();
            };
            items.Insert(insertAt++, archive);
            items.Insert(insertAt++, addBible);
            items.Insert(insertAt++, _env.CreateContextMenuItem("", null,
                CoreWebView2ContextMenuItemKind.Separator));
        }
        catch (Exception ex) { Log.Warn("contextmenu: " + ex.Message); }
    }

    private async Task ArchiveRecordAsync(string id)
    {
        var rec = BibleStore.Load().Records.FirstOrDefault(r => r.Id == id);
        if (rec == null) return;
        await ArchiveAsync(rec.Url, rec.Title, rec.Id);
        Flash("ARCHIVED · " + rec.Id);
    }

    // ------------------------------------------------------------------ chrome events

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMax(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxBtn_Click(object sender, RoutedEventArgs e) => ToggleMax();
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMax() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.View.CoreWebView2?.CanGoBack == true) _active.View.CoreWebView2.GoBack();
    }

    private void FwdBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.View.CoreWebView2?.CanGoForward == true) _active.View.CoreWebView2.GoForward();
    }

    private void ReloadBtn_Click(object sender, RoutedEventArgs e) => _active?.View.CoreWebView2?.Reload();
    private void HomeBtn_Click(object sender, RoutedEventArgs e) => Navigate(_settings.Homepage);
    private void NewTabBtn_Click(object sender, RoutedEventArgs e) => _ = CreateTabAsync(_settings.Homepage, true);

    private void BibleBtn_Click(object sender, RoutedEventArgs e) => Navigate(_home + "bible");
    private void DownloadsBtn_Click(object sender, RoutedEventArgs e) => Navigate(_home + "downloads");
    private void MenuBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = MenuBtn.ContextMenu;
        if (menu == null) return;
        menu.PlacementTarget = MenuBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Console / Forge presence in the title bar, refreshed every 5 s from the server side.</summary>
    private void StartLinkTimer()
    {
        _linkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _linkTimer.Tick += (_, _) => _ = RefreshLinksAsync();
        _linkTimer.Start();
        _ = RefreshLinksAsync();
    }

    private async Task RefreshLinksAsync()
    {
        try
        {
            var json = await Task.Run(() => LinkProbe.Snapshot());
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("links", out var links)) return;
            ApplyLink(links, "console", ConsoleDot, ConsolePillText);
            ApplyLink(links, "forge", ForgeDot, ForgePillText);
        }
        catch (Exception ex) { Log.Warn("links: " + ex.Message); }
    }

    private static readonly string ManagerBat =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LYGO Agent Manager.bat");
    private static readonly string ForgeBat = "<FORGE_DIR>\\LYGO Forge.bat";

    private void Pill_Enter(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = (Brush)FindResource("Gold");
    }

    private void Pill_Leave(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = (Brush)FindResource("Line");
    }

    private void ConsolePill_Click(object sender, MouseButtonEventArgs e) => Boot(ManagerBat, "console");
    private void ForgePill_Click(object sender, MouseButtonEventArgs e) => Boot(ForgeBat, "forge");

    /// <summary>Boot a LYGO service from the title-bar light, minimised so it stays out of the way.</summary>
    private void Boot(string bat, string label)
    {
        try
        {
            if (!File.Exists(bat)) { Flash("launcher missing: " + bat); return; }
            Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                WorkingDirectory = Path.GetDirectoryName(bat) ?? Paths.Root,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            });
            Flash("STARTING " + label.ToUpperInvariant() + " — watch the light turn green");
            Log.Info("booted " + label + " from title bar");
        }
        catch (Exception ex)
        {
            Flash("could not start " + label + ": " + ex.Message);
        }
    }

    private void ApplyLink(JsonElement links, string id, System.Windows.Shapes.Ellipse dot, TextBlock label)
    {
        if (!links.TryGetProperty(id, out var item)) return;
        var up = item.TryGetProperty("up", out var u) && u.ValueKind == JsonValueKind.True;
        dot.Fill = (Brush)FindResource(up ? "Mint" : "Danger");
        label.Text = up ? id.ToUpperInvariant() : id.ToUpperInvariant() + " OFF";
        label.Foreground = (Brush)FindResource(up ? "Muted" : "Danger");
    }

    private void MainMenu_Opened(object sender, RoutedEventArgs e)
    {
        // reserved: the menu is rebuilt by WPF each open; nothing to refresh yet
    }

    private void RefreshNavButtons()
    {
        if (_active?.View.CoreWebView2 == null) return;
        BackBtn.IsEnabled = _active.View.CoreWebView2.CanGoBack;
        FwdBtn.IsEnabled = _active.View.CoreWebView2.CanGoForward;
    }

    // ------------------------------------------------------------------ menu handlers

    private void MenuNewTab_Click(object sender, RoutedEventArgs e) => _ = CreateTabAsync(_settings.Homepage, true);
    private void MenuReopen_Click(object sender, RoutedEventArgs e) => ReopenClosed();
    private void MenuDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) return;
        _ = CreateTabAsync(_active.Url, true);
    }
    private void MenuFind_Click(object sender, RoutedEventArgs e) => OpenFind();
    private void MenuZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(0.1);
    private void MenuZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(-0.1);
    private void MenuZoomReset_Click(object sender, RoutedEventArgs e) => ZoomReset();
    private void MenuPrint_Click(object sender, RoutedEventArgs e) => DoPrint();
    private void MenuPdf_Click(object sender, RoutedEventArgs e) => _ = SavePdf();
    private void MenuSaveAs_Click(object sender, RoutedEventArgs e) => SavePageAs();
    private void MenuShot_Click(object sender, RoutedEventArgs e) => _ = SavePageShot();
    private void MenuSource_Click(object sender, RoutedEventArgs e) => ViewSource();
    private void MenuDevTools_Click(object sender, RoutedEventArgs e)
    {
        try { _active?.View.CoreWebView2?.OpenDevToolsWindow(); }
        catch (Exception ex) { Flash("devtools: " + ex.Message); }
    }
    private void MenuDownloads_Click(object sender, RoutedEventArgs e) => Navigate(_home + "downloads");
    private void MenuHistory_Click(object sender, RoutedEventArgs e) => Navigate(_home + "history");
    private void MenuBible_Click(object sender, RoutedEventArgs e) => Navigate(_home + "bible");
    private void MenuVault_Click(object sender, RoutedEventArgs e) => Navigate(_home + "vault");
    private void MenuArchive_Click(object sender, RoutedEventArgs e) => Navigate(_home + "archive");
    private void MenuSettings_Click(object sender, RoutedEventArgs e) => Navigate(_home + "settings");
    private void MenuAgent_Click(object sender, RoutedEventArgs e) => ShowAgentHelp();

    private void MenuAbout_Click(object sender, RoutedEventArgs e) => MessageBox.Show(
        $"LYGO BROWSER 1.9.1\n\nEngine      {_engine}\nBridge      http://127.0.0.1:{Paths.BridgePort}\n" +
        $"CDP         http://127.0.0.1:{Paths.CdpPort}\nBible       {BibleStore.Load().Records.Count} records\n" +
        $"Root        {Paths.Root}\n\nA LYGO browser for humans and agents. Built by Delta.",
        "ABOUT", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowAgentHelp() => MessageBox.Show(
        "AGENT API  (loopback only)\n\n" +
        $"Base     http://127.0.0.1:{Paths.BridgePort}\n" +
        "Token    data\\state\\bridge.token   (header X-LYGO-Token)\n\n" +
        "GET  /api/health\n" +
        "GET  /api/bible/search?q=lightpanda\n" +
        "POST /api/bible/add            {url,title,folder,tags,note}\n" +
        "POST /api/bible/archive        {id}\n" +
        "POST /api/agent/open           {url,newTab}\n" +
        "POST /api/agent/snapshot       {tab}\n" +
        "POST /api/agent/screenshot     {tab}\n" +
        "POST /api/agent/html           {tab}\n" +
        "POST /api/agent/eval           {js,tab}\n" +
        "POST /api/agent/click          {selector,tab}\n" +
        "POST /api/agent/type           {selector,text,tab}\n" +
        "GET  /api/agent/status\n" +
        "GET  /api/history?q=&limit=\n" +
        "GET  /api/downloads\n" +
        "POST /api/page/pdf             {}\n" +
        "POST /api/vault/fill           {id}\n\n" +
        $"CDP      http://127.0.0.1:{Paths.CdpPort}  (Playwright / Puppeteer / browser-use)\n" +
        "CLI      python tools\\lygo_agent.py status\n" +
        "MCP      python tools\\lygo_mcp_server.py",
        "LYGO BROWSER · AGENT", MessageBoxButton.OK, MessageBoxImage.Information);

    // ------------------------------------------------------------------ bookmark bar + status

    /// <summary>The bookmark bar: pinned favourites you click to open, plus pin and Bible entries.</summary>
    private void BuildBookmarkBar()
    {
        BookmarkBar.Children.Clear();
        var favs = BibleStore.Favourites(14);
        if (favs.Count == 0)
        {
            BookmarkBar.Children.Add(new TextBlock
            {
                Text = "\u2605 press the star (Ctrl+D) on a page to pin it here",
                Foreground = (Brush)FindResource("Muted"),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 10, 0)
            });
        }
        foreach (var fav in favs) BookmarkBar.Children.Add(FavouriteChip(fav));
        BookmarkBar.Children.Add(BarButton("+ LINK", "Pin the URL currently on your clipboard",
            null, PinFromClipboard));
        BookmarkBar.Children.Add(BarButton("\u2605 PIN", "Pin the current page (Ctrl+D)",
            (Brush)FindResource("Gold"), BookmarkCurrent));
        BookmarkBar.Children.Add(BarButton("BIBLE", "Open the Bookmark Bible", null,
            () => Navigate(_home + "bible")));
    }

    /// <summary>Pin a link copied from anywhere - the reliable alternative to dragging.</summary>
    private void PinFromClipboard()
    {
        string text = "";
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText() ?? ""; }
        catch (Exception ex) { Log.Warn("clipboard: " + ex.Message); }
        text = text.Trim();
        if (!LooksLikeUrl(text))
        {
            Flash("clipboard has no link — copy a URL first, then press + LINK");
            return;
        }
        var url = text.Contains("://") ? text : "https://" + text;
        var ok = BibleStore.SetFavourite(url, url, true);
        BuildBookmarkBar();
        Flash(ok ? "PINNED \u2605 from clipboard" : "COULD NOT PIN \u00b7 " + url);
    }

    private Button FavouriteChip(Bookmark fav)
    {
        var b = new Button
        {
            Content = Shorten(fav.Title, 26),
            Style = (Style)FindResource("TextButton"),
            FontSize = 10,
            Margin = new Thickness(0, 0, 3, 0),
            ToolTip = fav.Url + "  \u00b7  right-click to unpin"
        };
        b.Click += (_, _) => Navigate(fav.Url);
        var menu = new ContextMenu();
        var unpin = new MenuItem { Header = "Unpin from bar" };
        unpin.Click += (_, _) => { BibleStore.SetFavourite(fav.Url, fav.Title, false); BuildBookmarkBar(); Flash("UNPINNED \u00b7 " + fav.Title); };
        var bible = new MenuItem { Header = "Open in Bible" };
        bible.Click += (_, _) => Navigate(_home + "bible?id=" + fav.Id);
        menu.Items.Add(unpin);
        menu.Items.Add(bible);
        b.ContextMenu = menu;
        return b;
    }

    private Button BarButton(string label, string tip, Brush foreground, Action onClick)
    {
        var b = new Button
        {
            Content = label,
            Style = (Style)FindResource("TextButton"),
            Margin = new Thickness(8, 0, 2, 0),
            ToolTip = tip
        };
        if (foreground != null) b.Foreground = foreground;
        b.Click += (_, _) => onClick();
        return b;
    }

    private void BookmarkBar_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void BookmarkBar_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.Text)) return;
        var raw = (e.Data.GetData(DataFormats.Text) as string ?? "").Trim();
        if (raw.Length == 0 || !LooksLikeUrl(raw)) { Flash("that drop was not a link"); return; }
        var url = raw.Contains("://") ? raw : "https://" + raw;
        BibleStore.SetFavourite(url, url, true);
        BuildBookmarkBar();
        Flash("PINNED ★ " + url);
        UpdateCounts();
    }

    private void StarBtn_Click(object sender, RoutedEventArgs e) => BookmarkCurrent();

    private void BookmarkCurrent()
    {
        if (_active == null) return;
        var url = _active.View.CoreWebView2?.Source ?? _active.Url;
        var ownHud = url != null && (url.Contains("127.0.0.1:9337") || url.Contains("localhost:9337"));
        if (string.IsNullOrWhiteSpace(url) || ownHud)
        {
            Flash("the browser's own HUD is not pinned — local services like :9641 are");
            return;
        }
        try
        {
            var title = _active.View.CoreWebView2?.DocumentTitle ?? _active.Title;
            if (BibleStore.IsFavourite(url))
            {
                BibleStore.SetFavourite(url, title, false);
                BuildBookmarkBar();
                Flash("UNPINNED · " + Shorten(title, 40));
            }
            else
            {
                var ok = BibleStore.SetFavourite(url, title, true);
                BuildBookmarkBar();
                Flash(ok
                    ? "PINNED ★ " + Shorten(title, 40) + " — it is on the bar"
                    : "COULD NOT PIN · " + url);
            }
            UpdateCounts();
        }
        catch (Exception ex) { Flash("bookmark failed: " + ex.Message); }
    }

    private void UpdateCounts()
    {
        try
        {
            var doc = BibleStore.Load();
            var archived = Directory.Exists(Paths.ArchiveDir)
                ? Directory.GetDirectories(Paths.ArchiveDir).Length : 0;
            CountPillText.Text = $"BIBLE {doc.Records.Count(r => r.Status == "active")} · ARCH {archived}";
            StatusRight.Text = $"{EngineVersion()} · {Paths.Root}";
            UpdateDownloadsLabel();
        }
        catch { }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void Flash(string text)
    {
        SetStatus(text);
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); SetStatus("READY"); };
        _flashTimer.Start();
    }

    private static string Shorten(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..(max - 1)] + "…");

    // ------------------------------------------------------------------ keyboard

    private Dictionary<(Key, bool, bool), Action> _shortcuts;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && _findOpen) { CloseFind(); e.Handled = true; return; }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (alt && key == Key.Left) { BackBtn_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (alt && key == Key.Right) { FwdBtn_Click(this, new RoutedEventArgs()); e.Handled = true; return; }

        if (_shortcuts != null && _shortcuts.TryGetValue((key, ctrl, shift), out var action))
        {
            action();
            e.Handled = true;
        }
    }

    private void ReopenClosed()
    {
        if (_closed.Count == 0) { Flash("no closed tabs to reopen"); return; }
        var (url, _) = _closed.Pop();
        _ = CreateTabAsync(url, true);
    }

    // ------------------------------------------------------------------ IBrowserHost

    private Task<T> UiAsync<T>(Func<Task<T>> work) => Dispatcher.InvokeAsync(work).Task.Unwrap();

    private TabEntry ById(int? id) =>
        id.HasValue ? _tabs.FirstOrDefault(t => t.Id == id.Value) : _active;

    public async Task<string> OpenAsync(string url, bool newTab)
    {
        return await UiAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (newTab || _active == null) { await CreateTabAsync(url, true); return url; }
            Navigate(url);
            return url;
        });
    }

    public List<TabInfo> Tabs()
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(Tabs);
        return _tabs.Select(t => new TabInfo
        {
            Id = t.Id, Title = t.Title, Url = t.Url, Active = ReferenceEquals(t, _active)
        }).ToList();
    }

    public Task<string> GetTextAsync(int? tabId) => UiAsync(async () =>
    {
        var t = ById(tabId);
        if (t?.View.CoreWebView2 == null) return "";
        var raw = await t.View.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
        return JsonSerializer.Deserialize<string>(raw) ?? "";
    });

    public Task<object> SnapshotAsync(int? tabId) => UiAsync(async () =>
    {
        var t = ById(tabId);
        if (t?.View.CoreWebView2 == null) throw new InvalidOperationException("no active tab");
        var raw = await t.View.CoreWebView2.ExecuteScriptAsync(
            "JSON.stringify({title:document.title,url:location.href," +
            "text:(document.body?document.body.innerText:'').slice(0,200000)})");
        var inner = JsonSerializer.Deserialize<string>(raw) ?? "{}";
        using var doc = JsonDocument.Parse(inner);
        return (object)doc.RootElement.Clone();
    });

    public Task<string> HtmlAsync(int? tabId) => UiAsync(async () =>
    {
        var t = ById(tabId);
        if (t?.View.CoreWebView2 == null) return "";
        var raw = await t.View.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
        return JsonSerializer.Deserialize<string>(raw) ?? "";
    });

    public Task<string> EvalAsync(string js, int? tabId) => UiAsync(async () =>
    {
        var t = ById(tabId);
        if (t?.View.CoreWebView2 == null) return "";
        return await t.View.CoreWebView2.ExecuteScriptAsync(js ?? "");
    });

    public Task<string> ScreenshotAsync(int? tabId) => UiAsync(async () =>
    {
        var t = ById(tabId);
        if (t?.View.CoreWebView2 == null) return null;
        var png = await ArchiveService.CaptureFullPageAsync(t.View.CoreWebView2);
        if (png == null) return null;
        Directory.CreateDirectory(Paths.StateDir);
        var path = Path.Combine(Paths.StateDir, $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        await File.WriteAllBytesAsync(path, png);
        return path;
    });

    public Task<bool> ClickAsync(string selector, int? tabId) => UiAsync(async () =>
    {
        var t = ById(tabId);
        if (t?.View.CoreWebView2 == null || string.IsNullOrWhiteSpace(selector)) return false;
        var sel = JsonSerializer.Serialize(selector);
        var raw = await t.View.CoreWebView2.ExecuteScriptAsync(
            $"(function(){{var el=document.querySelector({sel});if(!el)return '0';el.click();return '1';}})()");
        return (JsonSerializer.Deserialize<string>(raw) ?? "0") == "1";
    });

    public Task<bool> TypeAsync(string selector, string text, int? tabId) => UiAsync(async () =>
    {
        var t = ById(tabId);
        if (t?.View.CoreWebView2 == null || string.IsNullOrWhiteSpace(selector)) return false;
        var sel = JsonSerializer.Serialize(selector);
        var val = JsonSerializer.Serialize(text ?? "");
        var raw = await t.View.CoreWebView2.ExecuteScriptAsync(
            $"(function(){{var el=document.querySelector({sel});if(!el)return '0';" +
            $"el.focus();el.value={val};" +
            "el.dispatchEvent(new Event('input',{bubbles:true}));" +
            "el.dispatchEvent(new Event('change',{bubbles:true}));return '1';})()");
        return (JsonSerializer.Deserialize<string>(raw) ?? "0") == "1";
    });

    public Task<bool> CloseTabAsync(int tabId) => UiAsync(async () =>
    {
        var t = _tabs.FirstOrDefault(x => x.Id == tabId);
        if (t == null) return false;
        CloseTab(t);
        await Task.CompletedTask;
        return true;
    });

    public async Task<object> ArchiveAsync(string url, string title, string id)
    {
        return await UiAsync(async () =>
        {
            TabEntry target = _active;
            TabEntry temp = null;
            var sameUrl = target != null && string.Equals(
                (target.View.CoreWebView2?.Source ?? target.Url).TrimEnd('/'),
                (url ?? "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
            if (!sameUrl)
            {
                temp = await CreateTabAsync(url, true);
                target = temp;
            }
            await WaitLoadedAsync(target, 25000);
            await Task.Delay(900);
            var res = await ArchiveService.CaptureAsync(target.View, url, title, id);
            BibleStore.MarkArchived(id, res.State, res.Sha256);
            if (temp != null) CloseTab(temp);
            UpdateCounts();
            return (object)new
            {
                ok = true, dir = $"archive/{id}", sha256 = res.Sha256,
                bytes = res.Bytes, files = res.Files
            };
        });
    }

    public Task<string> SavePdfAsync(int? tabId) => UiAsync(async () =>
    {
        await Task.CompletedTask;
        return await SavePdf();
    });

    /// <summary>Find-on-page, shared by Ctrl+F and the agent API.</summary>
    public Task<object> FindAsync(string text, bool forward, int? tabId) => UiAsync(async () =>
    {
        var t = ById(tabId);
        var core = t?.View.CoreWebView2;
        if (core == null || string.IsNullOrWhiteSpace(text))
            return (object)new { ok = false, error = "no tab or empty text" };
        var find = core.Find;
        try
        {
            if (find.MatchCount == 0)
            {
                var options = _env.CreateFindOptions();
                options.FindTerm = text;
                options.ShouldHighlightAllMatches = true;
                options.SuppressDefaultFindDialog = true;
                await find.StartAsync(options);
            }
            else if (forward) find.FindNext();
            else find.FindPrevious();
        }
        catch (Exception ex)
        {
            return (object)new { ok = false, error = ex.Message };
        }
        return (object)new
        {
            ok = find.MatchCount > 0,
            text,
            matches = find.MatchCount,
            index = find.MatchCount > 0 ? find.ActiveMatchIndex + 1 : 0
        };
    });

    public Task<object> SetZoomAsync(double factor, int? tabId) => UiAsync(async () =>
    {
        var t = ById(tabId);
        if (t?.View == null) return (object)new { ok = false, error = "no tab" };
        t.View.ZoomFactor = Math.Clamp(factor, 0.25, 5.0);
        if (ReferenceEquals(t, _active)) UpdateZoomLabel();
        await Task.CompletedTask;
        return (object)new { ok = true, zoom = t.View.ZoomFactor };
    });

    public List<HistoryEntry> HistoryEntries(int limit)
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => HistoryEntries(limit));
        return HistoryStore.Recent(limit);
    }

    public List<DownloadEntry> DownloadsList()
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => DownloadsList());
        return DownloadStore.All();
    }

    public Settings CurrentSettings()
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(CurrentSettings);
        return _settings;
    }

    public void ApplySettings(Settings s)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ApplySettings(s)); return; }
        _settings = s;
        SettingsStore.Save(s);
        Flash("SETTINGS SAVED");
    }

    private static async Task WaitLoadedAsync(TabEntry t, int timeoutMs)
    {
        if (t.Loaded) return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object _, CoreWebView2NavigationCompletedEventArgs ev) => tcs.TrySetResult(ev.IsSuccess);
        t.View.CoreWebView2.NavigationCompleted += Handler;
        try { await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)); }
        finally { t.View.CoreWebView2.NavigationCompleted -= Handler; }
    }

    public string EngineVersion() => _engine;
}
