using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WatchFlix.Core;
using WatchFlix.Core.Library;

namespace WatchFlix.Desktop;

/// <summary>Where you are: a tab, a title, a person, a settings page.</summary>
public sealed record Location(string Kind, long Id = 0, string Query = "")
{
    public string Title { get; set; } = "";

    public string Label => Kind switch
    {
        "home" => "Home",
        "movies" => "Movies",
        "shows" => "TV Shows",
        "actors" => "Actors",
        "settings" => "Settings",
        "search" => "search results",
        _ => Title.Length > 0 ? Title : "the last page",
    };
}

public sealed class MainWindow : Window
{
    public static MainWindow? Current { get; private set; }

    readonly Grid _root = new();
    readonly ContentControl _content = new() { Focusable = false };
    readonly Border _scanStrip = new();
    readonly TextBlock _scanText = Ui.Mono("", Theme.Paper, 12);
    readonly Grid _scanBar = new();
    readonly Border _toast = new();
    readonly TextBlock _toastText = Ui.Text("", 13.5, Theme.Paper, wrap: true);
    readonly Button _back = new();
    readonly TextBox _search = new();
    readonly Dictionary<string, Border> _tabs = new();
    readonly Dictionary<string, Border> _tabEdges = new();
    readonly Button _scanButton;
    readonly List<Location> _trail = new();
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4.2) };
    readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    public PlayerView Player { get; }
    public Location Here => _trail.Count > 0 ? _trail[^1] : new Location("home");

    public MainWindow()
    {
        Current = this;
        Title = "WatchFlix";
        Background = Theme.Vault;
        Foreground = Theme.Paper;
        FontFamily = Theme.Sans;
        Width = 1400;
        Height = 880;
        MinWidth = 900;
        MinHeight = 600;
        WindowState = WindowState.Maximized;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        App.DarkTitleBar(this);
        TrySetIcon();
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _scanButton = Ui.Button("Scan", () => StartScan(false));
        Player = new PlayerView(this);

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = BuildTopBar();
        _root.Children.Add(bar);
        BuildScanStrip();
        Grid.SetRow(_scanStrip, 1);
        _root.Children.Add(_scanStrip);
        Grid.SetRow(_content, 2);
        _root.Children.Add(_content);

        Grid.SetRowSpan(Player, 3);
        Player.Visibility = Visibility.Collapsed;
        _root.Children.Add(Player);

        BuildToast();
        Grid.SetRowSpan(_toast, 3);
        _root.Children.Add(_toast);
        Content = _root;

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Visibility = Visibility.Collapsed; };
        _scanTimer.Tick += (_, _) => PollScan();
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = Suggest(); };
        Scanner.Progress += () => Dispatcher.BeginInvoke(() => { if (!_scanTimer.IsEnabled) _scanTimer.Start(); });

        // Keys are caught for every window in the app, not just this one. The
        // player's controls sit in a separate see-through window laid over the
        // video (and the pop-out has one of its own); once one of those had
        // been clicked, it had the keyboard and the shortcuts went nowhere.
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnAnyWindowKey));
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; GoBack(); }
        };
        Loaded += async (_, _) =>
        {
            Navigate(new Location("home"));
            if (Scanner.Running) _scanTimer.Start();
            // Get the video player loaded while nobody is waiting on it.
            await Task.Delay(1500);
            _ = Player.EnsureVlcAsync();
        };
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source) source.AddHook(PaintVideoHostBlack);
        };
        Closing += (_, _) =>
        {
            Player.Shutdown();
        };
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern IntPtr GetStockObject(int index);

    /// <summary>
    /// The video player draws into a plain Windows "static" window. Wherever
    /// VLC's picture does not cover it — for a moment while a file opens, or
    /// around the edges when the window changes size — Windows paints it in
    /// the light grey of an old dialog box, which is the white that showed up
    /// over the video. Answering its colour request with black makes those
    /// moments invisible.
    /// </summary>
    internal static IntPtr PaintVideoHostBlack(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_CTLCOLORSTATIC = 0x0138;
        const int BLACK_BRUSH = 4;
        if (msg != WM_CTLCOLORSTATIC) return IntPtr.Zero;
        handled = true;
        return GetStockObject(BLACK_BRUSH);
    }

    void TrySetIcon()
    {
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/watchflix.ico"));
        }
        catch { }
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // -------------------------------------------------------------- top bar
    FrameworkElement BuildTopBar()
    {
        var grid = new Grid { Height = 62, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x18)) };
        for (var i = 0; i < 9; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        // Drawn rather than typed: a text arrow sits on the font's baseline, so it
        // never lands in the middle of its box.
        var chevron = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M6,1 L1,6 L6,11"),
            Stroke = Theme.Dim,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 7, Height = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 1, 0),
        };
        _back.Content = chevron;
        _back.Width = 34;
        _back.Height = 34;
        _back.Padding = new Thickness(0);
        _back.MouseEnter += (_, _) => chevron.Stroke = Theme.Paper;
        _back.MouseLeave += (_, _) => chevron.Stroke = Theme.Dim;
        _back.Margin = new Thickness(24, 0, 26, 0);
        _back.IsEnabled = false;
        _back.Click += (_, _) => GoBack();
        Place(grid, _back, 0);

        // The same mark and wordmark as the web pages: a lamp-coloured bar with a
        // dimmer one behind it, then WATCHFLIX in heavy capitals, FLIX in amber.
        var mark = new Canvas { Width = 6, Height = 24, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var back2 = new System.Windows.Shapes.Rectangle { Width = 2, Height = 20, Fill = Theme.LampDim };
        Canvas.SetLeft(back2, 12);
        Canvas.SetTop(back2, 2);
        mark.Children.Add(back2);
        mark.Children.Add(new System.Windows.Shapes.Rectangle { Width = 6, Height = 24, Fill = Theme.Lamp, RadiusX = 1, RadiusY = 1 });
        var brand = new Tracked { Family = new FontFamily("Segoe UI"), Size = 15.9, Em = -0.03, VerticalAlignment = VerticalAlignment.Center }
            .Add("WATCH", Theme.Paper, FontWeights.ExtraBold)
            .Add("FLIX", Theme.Lamp, FontWeights.ExtraBold);
        // Just the name: Home is the first tab right beside it.
        var brandMark = Ui.Row(mark, brand);
        brandMark.Margin = new Thickness(0, 0, 26, 0);
        brandMark.VerticalAlignment = VerticalAlignment.Center;
        Place(grid, brandMark, 1);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
        foreach (var (key, label) in new[] { ("home", "Home"), ("movies", "Movies"), ("shows", "TV Shows"), ("actors", "Actors") })
        {
            var text = Ui.Text(label, 13.5, Theme.Dim, FontWeights.SemiBold);
            text.VerticalAlignment = VerticalAlignment.Center;
            // A pill, lit on the current tab with an amber edge — as on the web pages.
            var pill = new Border
            {
                Child = text,
                BorderThickness = new Thickness(1),
                BorderBrush = Theme.Clear,
                Background = Theme.Clear,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 4, 0),
            };
            var edge = new Border { Width = 3, Background = Theme.Clear, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3, 0, 0, 3) };
            var host = new Grid { VerticalAlignment = VerticalAlignment.Center };
            host.Children.Add(pill);
            host.Children.Add(edge);
            host.Tag = edge;
            var b = Ui.Bare(host, () => Navigate(new Location(key)));
            b.VerticalAlignment = VerticalAlignment.Center;
            b.MouseEnter += (_, _) => { if (edge.Background == Theme.Clear) { pill.Background = Theme.Shelf; text.Foreground = Theme.Paper; } };
            b.MouseLeave += (_, _) => { if (edge.Background == Theme.Clear) { pill.Background = Theme.Clear; text.Foreground = Theme.Dim; } };
            _tabs[key] = pill;
            _tabEdges[key] = edge;
            tabs.Children.Add(b);
        }
        Place(grid, tabs, 2);

        _search.Width = 260;
        _search.Height = 34;
        _search.Background = Theme.Shelf;
        _search.FontSize = 13.2;
        _search.Padding = new Thickness(32, 0, 34, 0);
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        var searchHost = new Grid { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var hint = Ui.Text("Search titles, cast, files", 13.2, Theme.Dimmer, margin: new Thickness(34, 0, 0, 0));
        var magnifier = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M6.5,1.5 A5,5 0 1 1 6.49,1.5 Z M10.2,10.2 L14,14"),
            Stroke = Theme.Dim, StrokeThickness = 1.7, StrokeEndLineCap = PenLineCap.Round,
            Width = 16, Height = 16, Margin = new Thickness(11, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        var kbd = new Border
        {
            BorderBrush = Theme.Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = Ui.Mono("/", Theme.Dimmer, 10.5),
        };
        _search.IsKeyboardFocusWithinChanged += (_, _) => kbd.Visibility = _search.IsKeyboardFocusWithin ? Visibility.Hidden : Visibility.Visible;
        searchHost.Children.Add(_search);
        searchHost.Children.Add(hint);
        searchHost.Children.Add(kbd);
        searchHost.Children.Add(magnifier);
        BuildSuggestions(searchHost);
        Place(grid, searchHost, 5);

        _scanButton.Margin = new Thickness(0, 0, 10, 0);
        _scanButton.VerticalAlignment = VerticalAlignment.Center;
        Place(grid, _scanButton, 6);

        var settings = Ui.Button("Settings", () => Navigate(new Location("settings")));
        settings.Margin = new Thickness(0, 0, 24, 0);
        settings.VerticalAlignment = VerticalAlignment.Center;
        Place(grid, settings, 8);

        var line = new Border { BorderBrush = Theme.Rail, BorderThickness = new Thickness(0, 0, 0, 1) };
        Grid.SetColumnSpan(line, 9);
        grid.Children.Insert(0, line);
        return grid;
    }

    static void Place(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    void SetTab(string kind)
    {
        foreach (var (key, border) in _tabs)
        {
            var on = key == kind;
            border.BorderBrush = on ? Theme.Edge : Theme.Clear;
            border.Background = on ? Theme.Shelf : Theme.Clear;
            _tabEdges[key].Background = on ? Theme.Lamp : Theme.Clear;
            if (border.Child is TextBlock t) t.Foreground = on ? Theme.Paper : Theme.Dim;
        }
    }

    // ------------------------------------------------------------ scan strip
    void BuildScanStrip()
    {
        _scanStrip.Background = Theme.Shelf;
        _scanStrip.BorderBrush = Theme.Rail;
        _scanStrip.BorderThickness = new Thickness(0, 0, 0, 1);
        _scanStrip.Padding = new Thickness(24, 8, 24, 8);
        _scanStrip.Visibility = Visibility.Collapsed;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _scanBar.Height = 4;
        _scanBar.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_scanBar);
        _scanText.Margin = new Thickness(16, 0, 16, 0);
        _scanText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_scanText, 1);
        grid.Children.Add(_scanText);
        var details = Ui.Button("Details", ShowScanLog);
        details.Padding = new Thickness(10, 3, 10, 3);
        Grid.SetColumn(details, 2);
        grid.Children.Add(details);
        _scanStrip.Child = grid;
    }

    public void StartScan(bool rescrape)
    {
        if (Settings.AllRoots().Count == 0)
        {
            Toast("Add a library folder first.", true);
            Navigate(new Location("settings"));
            return;
        }
        if (Scanner.StartScan(rescrape))
        {
            _scanTimer.Start();
        }
        else Toast("A scan is already running.");
    }

    public void StartPreviewRebuild()
    {
        if (Scanner.StartPreviewRebuild())
        {
            _scanTimer.Start();
        }
    }

    bool _scanWasRunning;

    void PollScan()
    {
        var s = Scanner.Snapshot();
        if (s.Running)
        {
            _scanWasRunning = true;
            _scanStrip.Visibility = Visibility.Visible;
            _scanButton.IsEnabled = false;
            var fraction = s.Total > 0 ? (double)s.Done / s.Total : 0;
            _scanBar.Children.Clear();
            _scanBar.Children.Add(Ui.ProgressBar(fraction, 4));
            var text = $"{s.Phase} — {s.Done}/{s.Total}  {s.Current}";
            _scanText.Text = text.Length > 140 ? text[..140] : text;
            return;
        }
        _scanTimer.Stop();
        _scanButton.IsEnabled = true;
        if (!_scanWasRunning) return;
        _scanWasRunning = false;
        _scanStrip.Visibility = Visibility.Collapsed;
        Images.Clear();
        Toast($"Scan done — {s.Added} added, {s.Scraped} scraped" + (s.Missing > 0 ? $", {s.Missing} missing" : ""));
        if (!Player.IsOpen) Refresh();
    }

    void ShowScanLog()
    {
        var s = Scanner.Snapshot();
        var box = new TextBox
        {
            Text = s.Log.Count > 0 ? string.Join(Environment.NewLine, s.Log) : "Nothing logged yet.",
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Theme.Mono,
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
        };
        Dialogs.Show(this, "Scan log", box, 720, 480);
    }

    // ----------------------------------------------------------------- toast
    void BuildToast()
    {
        _toast.Background = Theme.Shelf;
        _toast.BorderBrush = Theme.Lamp;
        _toast.BorderThickness = new Thickness(3, 0, 0, 0);
        _toast.CornerRadius = new CornerRadius(3);
        _toast.Padding = new Thickness(18, 11, 18, 11);
        _toast.MaxWidth = 560;
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.VerticalAlignment = VerticalAlignment.Bottom;
        _toast.Margin = new Thickness(0, 0, 0, 34);
        _toast.Visibility = Visibility.Collapsed;
        _toast.IsHitTestVisible = false;
        _toast.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, Opacity = 0.5, ShadowDepth = 6 };
        _toast.Child = _toastText;
        Panel.SetZIndex(_toast, 100);
    }

    public void Toast(string message, bool bad = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Toast(message, bad));
            return;
        }
        _toastText.Text = message;
        _toast.BorderBrush = bad ? Theme.Cert : Theme.Lamp;
        _toast.Visibility = Visibility.Visible;
        Player.MirrorToast(message, bad);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ------------------------------------------------------------ navigation
    public void Navigate(Location where, bool push = true)
    {
        if (push)
        {
            if (_trail.Count > 0 && _trail[^1] == where) push = false;
            else
            {
                _trail.Add(where);
                if (_trail.Count > 60) _trail.RemoveAt(0);
            }
        }
        UpdateBack();
        _ = Render(where);
    }

    public void Refresh() => _ = Render(Here);

    /// <summary>Called by a page once it knows its own name, so Back can say "Back to Breaking Bad".</summary>
    public void NamePage(Location where, string title)
    {
        where.Title = title;
        UpdateBack();
    }

    public void GoBack()
    {
        if (Player.IsOpen)
        {
            _ = Player.CloseAsync();
            return;
        }
        if (Dialogs.CloseTop()) return;
        if (_trail.Count < 2) return;
        _trail.RemoveAt(_trail.Count - 1);
        UpdateBack();
        _ = Render(_trail[^1]);
    }

    public void UpdateBack()
    {
        var target = _trail.Count > 1 ? _trail[^2] : null;
        _back.IsEnabled = target != null || Player.IsOpen;
        _back.ToolTip = null;
    }

    int _renderToken;

    async Task Render(Location where)
    {
        var token = ++_renderToken;
        SetTab(where.Kind switch
        {
            "movie" => "movies",
            "show" => "shows",
            "person" => "actors",
            _ => where.Kind,
        });
        _content.Content = Ui.Loading();
        FrameworkElement page;
        try
        {
            page = await Pages.Build(this, where);
        }
        catch (Exception ex)
        {
            App.Log($"Page {where} failed: {ex}");
            page = Ui.Empty("Something went wrong", ex.Message, Ui.Button("Try again", Refresh, true));
        }
        if (token != _renderToken) return;       // something newer was asked for meanwhile
        _content.Content = page;
        if (page is ScrollViewer sv) sv.ScrollToTop();
    }

    // ----------------------------------------------------- search as you type
    // A list drops from the search box while typing: poster, title, year.
    // Arrow keys move through it, Enter opens the lit one (or every result
    // when none is lit), Esc closes it.
    readonly System.Windows.Controls.Primitives.Popup _suggest = new()
    {
        Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        StaysOpen = true,
        AllowsTransparency = true,
        VerticalOffset = 6,
        PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
    };
    readonly StackPanel _suggestList = new();
    readonly List<(Border Face, Action Open)> _suggestRows = new();
    int _suggestIndex = -1;
    int _suggestVersion;
    const double SuggestWidth = 340;

    void BuildSuggestions(FrameworkElement anchor)
    {
        _suggest.PlacementTarget = anchor;
        _suggest.HorizontalOffset = -(SuggestWidth - 260);      // right edges line up with the box
        _suggest.Child = new Border
        {
            Width = SuggestWidth,
            Background = Theme.Shelf,
            BorderBrush = Theme.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Child = _suggestList,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 8, Direction = 270, Opacity = 0.5 },
        };
        _search.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down when _suggest.IsOpen:
                    MoveSuggestion(1);
                    e.Handled = true;
                    break;
                case Key.Up when _suggest.IsOpen:
                    MoveSuggestion(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (_suggest.IsOpen && _suggestIndex >= 0 && _suggestIndex < _suggestRows.Count)
                        _suggestRows[_suggestIndex].Open();
                    else RunSearch();
                    e.Handled = true;
                    break;
                case Key.Escape when _suggest.IsOpen:
                    CloseSuggestions();
                    e.Handled = true;
                    break;
            }
        };
        _search.LostKeyboardFocus += (_, _) =>
        {
            // A click on a row lands after focus leaves; let it through first.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!_search.IsKeyboardFocusWithin && !_suggestList.IsMouseOver) CloseSuggestions();
            });
        };
        _search.GotKeyboardFocus += (_, _) =>
        {
            if (_search.Text.Trim().Length > 0 && _suggestRows.Count > 0) _suggest.IsOpen = true;
        };
        Deactivated += (_, _) => CloseSuggestions();
        PreviewMouseDown += (_, _) =>
        {
            if (_suggest.IsOpen && !anchor.IsMouseOver) CloseSuggestions();
        };
        LocationChanged += (_, _) => CloseSuggestions();
    }

    void CloseSuggestions()
    {
        _suggest.IsOpen = false;
        _suggestIndex = -1;
        PaintSuggestions();
    }

    void MoveSuggestion(int step)
    {
        if (_suggestRows.Count == 0) return;
        _suggestIndex = Math.Clamp(_suggestIndex + step, -1, _suggestRows.Count - 1);
        PaintSuggestions();
    }

    void PaintSuggestions()
    {
        for (var i = 0; i < _suggestRows.Count; i++)
            _suggestRows[i].Face.Background = i == _suggestIndex ? Theme.Rail : Theme.Clear;
    }

    /// <summary>Best first: an exact title, then titles starting with it, then a word starting with it.</summary>
    static int SuggestRank(string title, string q)
    {
        var t = title.ToLowerInvariant();
        if (t == q) return 0;
        if (t.StartsWith(q, StringComparison.Ordinal)) return 1;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"(^|[^a-z0-9])" + System.Text.RegularExpressions.Regex.Escape(q))) return 2;
        if (t.Contains(q, StringComparison.Ordinal)) return 3;
        return 4;
    }

    async Task Suggest()
    {
        var q = _search.Text.Trim();
        var version = ++_suggestVersion;
        if (q.Length == 0)
        {
            _suggestRows.Clear();
            _suggestList.Children.Clear();
            CloseSuggestions();
            return;
        }
        var page = await Task.Run(() => Catalog.LibraryPage(null, null, q, "title", limit: 60));
        if (version != _suggestVersion || _search.Text.Trim() != q) return;       // typed on since
        var lower = q.ToLowerInvariant();
        var hits = page.Items
            .OrderBy(i => SuggestRank(i.Str("title"), lower))
            .ThenBy(i => i.Str("title"), StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        _suggestRows.Clear();
        _suggestList.Children.Clear();
        _suggestIndex = -1;
        if (hits.Count == 0)
            _suggestList.Children.Add(Ui.Mono("No matches", Theme.Dimmer, 12).Margin(14, 14, 14, 14));
        foreach (var item in hits)
        {
            var hit = item;
            var art = new Border
            {
                Width = 46, Height = 69, CornerRadius = new CornerRadius(2), Background = Theme.Rail,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var poster = hit.Truthy("poster") ? hit.Str("poster") : hit.Str("thumb");
            if (poster.Length > 0) Ui.Cover(art, poster, decode: 92);
            var title = Ui.Text(hit.Str("title"), 14, Theme.Paper, FontWeights.Bold, wrap: true);
            title.MaxHeight = 38;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            var facts = new List<string>();
            if (hit.Truthy("year")) facts.Add(hit.Str("year"));
            facts.Add(hit.Str("kind") == "show" ? "Series" : "Film");
            var text = Ui.Column(title, Ui.Mono(string.Join(" · ", facts), Theme.Dim, 11.7).Margin(0, 6, 0, 0));
            text.Margin = new Thickness(14, 0, 0, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel { LastChildFill = true };
            row.Children.Add(art);
            row.Children.Add(text);
            var face = new Border
            {
                Padding = new Thickness(12, 9, 12, 9),
                BorderBrush = Theme.Rail,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background = Theme.Clear,
                Child = row,
            };
            void Open()
            {
                CloseSuggestions();
                _search.Text = "";
                Keyboard.ClearFocus();
                Navigate(new Location(hit.Str("kind") == "show" ? "show" : "movie", hit.Long("id") ?? 0) { Title = hit.Str("title") });
            }
            var index = _suggestRows.Count;
            var button = Ui.Bare(face, Open);
            button.Focusable = false;
            button.MouseEnter += (_, _) => { _suggestIndex = index; PaintSuggestions(); };
            _suggestRows.Add((face, Open));
            _suggestList.Children.Add(button);
        }
        if (page.Total > hits.Count)
        {
            var all = new Border
            {
                Padding = new Thickness(14, 11, 14, 11),
                Background = Theme.Clear,
                Child = Ui.Text($"All {page.Total} results", 12.75, Theme.Lamp, FontWeights.SemiBold),
            };
            var allButton = Ui.Bare(all, RunSearch);
            allButton.Focusable = false;
            var index = _suggestRows.Count;
            allButton.MouseEnter += (_, _) => { _suggestIndex = index; PaintSuggestions(); };
            _suggestRows.Add((all, RunSearch));
            _suggestList.Children.Add(allButton);
        }
        if (_search.IsKeyboardFocusWithin) _suggest.IsOpen = true;
    }

    void RunSearch()
    {
        CloseSuggestions();
        var q = _search.Text.Trim();
        if (q.Length == 0)
        {
            if (Here.Kind == "search") GoBack();
            return;
        }
        var where = new Location("search", 0, q);
        if (Here.Kind == "search")
        {
            _trail[^1] = where;
            _ = Render(where);
        }
        else Navigate(where);
    }

    // -------------------------------------------------------------- keyboard
    static bool IsVideoOverlay(Window w) => w.GetType().Name == "ForegroundWindow";

    void OnAnyWindowKey(object sender, KeyEventArgs e)
    {
        if (sender is not Window w || e.Handled) return;
        if (w is PopOutWindow || (IsVideoOverlay(w) && w.Owner is PopOutWindow))
            Player.PopKey(e);
        else if (ReferenceEquals(w, this) || (IsVideoOverlay(w) && ReferenceEquals(w.Owner, this)))
            OnKey(this, e);
        // Dialogs (Edit details and the like) keep their own keys.
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (Player.IsOpen)
        {
            Player.HandleKey(e);
            return;
        }
        var typing = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.Escape)
        {
            if (Dialogs.CloseTop()) { e.Handled = true; return; }
            if (typing && Keyboard.FocusedElement == _search) { Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this, this); e.Handled = true; }
            return;
        }
        if (!typing && (e.Key == Key.OemQuestion || e.Key == Key.Divide))
        {
            _search.Focus();
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) || e.Key == Key.BrowserBack ||
            (e.Key == Key.Back && !typing))
        {
            GoBack();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------- playback
    public void Play(long fileId, double position = 0)
    {
        _ = Player.OpenAsync(fileId, position);
        UpdateBack();
    }
}
