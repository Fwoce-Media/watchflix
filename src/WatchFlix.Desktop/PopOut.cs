using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using LibVLCSharp.WPF;
using WatchFlix.Core;

namespace WatchFlix.Desktop;

/// <summary>
/// The pop-out player: a small borderless window that stays above everything
/// else, laid out like YouTube's picture-in-picture window. Drag it anywhere,
/// pull any edge to resize it. The controls show while the mouse moves over it
/// and fade five seconds after it stops, or as soon as a drag begins.
/// </summary>
public sealed class PopOutWindow : Window
{
    const double Grip = 7;               // how close to an edge counts as the edge
    const double IdleSeconds = 5;

    readonly PlayerView _player;
    readonly Grid _overlay = new() { Background = Theme.HitTarget };
    readonly Grid _chrome = new() { Opacity = 0, IsHitTestVisible = false };
    readonly TextBlock _title = Ui.Text("", 13, Theme.Paper, FontWeights.SemiBold);
    readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Focusable = false };
    readonly TextBlock _time = Ui.Text("", 12.5, Theme.Paper);
    readonly Border _playFace = new();
    readonly Button _mute;
    readonly Button _cc;
    readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(IdleSeconds) };
    readonly TaskCompletionSource<bool> _ready = new();
    Point _lastMouse = new(double.NaN, double.NaN);
    bool _shown;
    bool _seeking;
    bool _closingFromPlayer;

    public VideoView View { get; }
    public Task Ready => _ready.Task;
    public bool Seeking => _seeking;

    public PopOutWindow(PlayerView player, string title, ImageSource? icon)
    {
        _player = player;
        Title = "WatchFlix · " + title;
        Icon = icon;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        ShowInTaskbar = true;
        Background = Brushes.Black;
        MinWidth = 280;
        MinHeight = 158;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // No frame, but a real resizable window underneath, so Windows does the
        // moving and resizing when asked.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(Grip),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        PlaceFromLastTime();

        _title.Text = title;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;

        _mute = IconButton(Glyphs.Speaker(false), "Mute", () => player.PopToggleMute());
        _cc = IconButton(Glyphs.Captions(false), "Subtitles", () => player.OpenTrackMenu(_cc!));
        BuildChrome();

        View = new VideoView { Background = Brushes.Black, Content = _overlay };
        View.Loaded += (_, _) => _ready.TrySetResult(true);
        Content = View;

        _idle.Tick += (_, _) => HideChrome();
        _overlay.MouseMove += OnMouseMove;
        _overlay.MouseLeave += (_, _) =>
        {
            _lastMouse = new Point(double.NaN, double.NaN);
            if (!_player.MenuOpen && !_seeking) HideChrome();
        };
        _overlay.MouseLeftButtonDown += OnMouseDown;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            try
            {
                var round = 2;           // DWMWCP_ROUND: Windows 11 rounds the corners
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
                var dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            }
            catch { }
            HwndSource.FromHwnd(hwnd)?.AddHook(MainWindow.PaintVideoHostBlack);
        };
        Closing += (_, e) =>
        {
            SaveBounds();
            if (!_closingFromPlayer)
            {
                // Closed from the taskbar or with Alt+F4: that means stop watching.
                // The player stops the video first, then closes this window itself.
                e.Cancel = true;
                _ = _player.CloseAsync();
                return;
            }
            _idle.Stop();
        };
    }

    /// <summary>Closed by the player itself, which has already dealt with playback.</summary>
    public void CloseFromPlayer()
    {
        _closingFromPlayer = true;
        Close();
    }

    // -------------------------------------------------------------- layout
    void BuildChrome()
    {
        // YouTube darkens the whole picture a little while its controls show.
        _chrome.Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        _chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chrome.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // top: the name on the left, window buttons on the right
        var top = new DockPanel { Margin = new Thickness(12, 8, 8, 0), LastChildFill = true };
        var buttons = Ui.Row(
            IconButton(Glyphs.Minimise(), "Minimise", () => WindowState = WindowState.Minimized),
            IconButton(Glyphs.BackToApp(), "Back to WatchFlix", () => _ = _player.ReturnFromPopOut()),
            IconButton(Glyphs.Close(), "Close", () => _ = _player.CloseAsync()));
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        var mark = new Border { Width = 3, Height = 14, Background = Theme.Lamp, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _title.VerticalAlignment = VerticalAlignment.Center;
        var name = new DockPanel { LastChildFill = true, IsHitTestVisible = false };
        name.Children.Add(mark);
        name.Children.Add(_title);
        top.Children.Add(name);
        _chrome.Children.Add(top);

        // middle: back ten seconds, play or pause, forward ten seconds
        var play = new Button
        {
            Width = 56, Height = 56, Padding = new Thickness(0), Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Play",
            Template = RoundTemplate(),
            Background = Theme.Lamp,
            Content = _playFace,
        };
        play.Click += (_, _) => _player.TogglePause();
        SetPlaying(true);
        var back = IconButton(Glyphs.Skip(forward: false, size: 28), "Back 10", () => _player.PopSkip(-10), 44);
        var fwd = IconButton(Glyphs.Skip(forward: true, size: 28), "Forward 10", () => _player.PopSkip(10), 44);
        back.Margin = new Thickness(0, 0, 22, 0);
        fwd.Margin = new Thickness(22, 0, 0, 0);
        var middle = Ui.Row(back, play, fwd);
        middle.HorizontalAlignment = HorizontalAlignment.Center;
        middle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(middle, 1);
        _chrome.Children.Add(middle);

        // bottom: the seek bar, then the time, sound and subtitles
        _seek.Margin = new Thickness(0, 0, 0, 2);
        _seek.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        _seek.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _seeking = false;
            _player.PopSeek(_seek.Value);
        };
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _seeking = false;
            _player.PopSeek(_seek.Value);
        }));
        var row = new DockPanel { LastChildFill = false };
        _time.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_time);
        var right = Ui.Row(_mute, _cc);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var bottom = Ui.Column(_seek, row);
        bottom.Margin = new Thickness(16, 0, 12, 8);
        Grid.SetRow(bottom, 2);
        _chrome.Children.Add(bottom);

        _overlay.Children.Add(_chrome);
    }

    static ControlTemplate RoundTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var ellipse = new FrameworkElementFactory(typeof(Border));
        ellipse.SetValue(Border.CornerRadiusProperty, new CornerRadius(28));
        ellipse.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        ellipse.AppendChild(presenter);
        template.VisualTree = ellipse;
        return template;
    }

    static Button IconButton(UIElement glyph, string tip, Action click, double size = 32)
    {
        var face = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = Theme.Clear, Child = glyph,
        };
        var b = Ui.Bare(face, click, tip);
        b.Focusable = false;
        b.VerticalAlignment = VerticalAlignment.Center;
        b.MouseEnter += (_, _) => face.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        b.MouseLeave += (_, _) => face.Background = Theme.Clear;
        return b;
    }

    // ------------------------------------------------------- state from player
    public void SetPlaying(bool playing) => _playFace.Child = playing ? Glyphs.Pause(Ui.Ink) : Glyphs.Play(Ui.Ink);

    public void SetTime(double now, double total)
    {
        if (!_seeking)
        {
            _seek.Maximum = Math.Max(1, total);
            _seek.Value = Math.Min(now, _seek.Maximum);
        }
        _time.Text = total > 0 ? $"{Ui.Clock(_seeking ? _seek.Value : now)} / {Ui.Clock(total)}" : "";
    }

    public void SetMuted(bool silent) => ((Border)_mute.Content).Child = Glyphs.Speaker(silent);

    public void SetCaptions(bool on) => ((Border)_cc.Content).Child = Glyphs.Captions(on);

    public void SetTitle(string title)
    {
        _title.Text = title;
        Title = "WatchFlix · " + title;
    }

    // ------------------------------------------------------ showing the controls
    void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        _overlay.Cursor = CursorFor(Edge(p));
        // WPF reports a "move" when things change under a still pointer too;
        // only a real move should bring the controls back.
        if (!double.IsNaN(_lastMouse.X) && Math.Abs(p.X - _lastMouse.X) < 1 && Math.Abs(p.Y - _lastMouse.Y) < 1) return;
        _lastMouse = p;
        ShowChrome();
    }

    public void ShowChrome()
    {
        _idle.Stop();
        _idle.Start();
        if (_shown) return;
        _shown = true;
        _chrome.IsHitTestVisible = true;
        _chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
    }

    public void HideChrome(bool now = false)
    {
        _idle.Stop();
        if (!now && (_player.MenuOpen || _seeking)) { _idle.Start(); return; }
        if (!_shown) return;
        _shown = false;
        _chrome.IsHitTestVisible = false;
        _chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(now ? 80 : 350)));
    }

    // --------------------------------------------------- dragging and resizing
    const int WM_NCLBUTTONDOWN = 0xA1;
    const int HTCAPTION = 2;

    int Edge(Point p)
    {
        var w = _overlay.ActualWidth;
        var h = _overlay.ActualHeight;
        var left = p.X < Grip;
        var right = p.X > w - Grip;
        var top = p.Y < Grip;
        var bottom = p.Y > h - Grip;
        if (top && left) return 13;
        if (top && right) return 14;
        if (bottom && left) return 16;
        if (bottom && right) return 17;
        if (left) return 10;
        if (right) return 11;
        if (top) return 12;
        if (bottom) return 15;
        return HTCAPTION;
    }

    static Cursor? CursorFor(int hit) => hit switch
    {
        10 or 11 => Cursors.SizeWE,
        12 or 15 => Cursors.SizeNS,
        13 or 17 => Cursors.SizeNWSE,
        14 or 16 => Cursors.SizeNESW,
        _ => null,
    };

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons and the seek bar take their own clicks before this is reached.
        var hit = Edge(e.GetPosition(_overlay));
        if (hit == HTCAPTION) HideChrome(now: true);     // the controls go while it moves
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        e.Handled = true;
        ReleaseCapture();
        // Hands the drag to Windows, exactly as if the frame had been grabbed.
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)hit, IntPtr.Zero);
        _lastMouse = new Point(double.NaN, double.NaN);
    }

    // ------------------------------------------------------ where it sits
    void PlaceFromLastTime()
    {
        var area = SystemParameters.WorkArea;
        double w = 480, h = 270, l = area.Right - 480 - 24, t = area.Bottom - 270 - 24;
        var saved = Settings.Str("popout_bounds").Split(',');
        if (saved.Length == 4 &&
            double.TryParse(saved[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sl) &&
            double.TryParse(saved[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var st) &&
            double.TryParse(saved[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sw) &&
            double.TryParse(saved[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var sh) &&
            sw >= MinWidth && sh >= MinHeight &&
            sl + sw > SystemParameters.VirtualScreenLeft + 40 && st + 40 > SystemParameters.VirtualScreenTop &&
            sl < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 &&
            st < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
        {
            (l, t, w, h) = (sl, st, sw, sh);
        }
        Left = l;
        Top = t;
        Width = w;
        Height = h;
    }

    void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        Settings.Set("popout_bounds", string.Join(",", new[] { Left, Top, Width, Height }
            .Select(v => Math.Round(v).ToString(CultureInfo.InvariantCulture))));
    }

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>
/// Line icons for the player controls. Every one is drawn on the same 20×20
/// square with its weight in the middle, so any row of them lines up.
/// </summary>
public static class Glyphs
{
    const double Size = 20;

    static System.Windows.Shapes.Path Line(string data, Brush? stroke = null, double thickness = 1.8) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = stroke ?? Theme.Paper,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    static System.Windows.Shapes.Path Solid(string data, Brush? fill = null) => new()
    {
        Data = Geometry.Parse(data),
        Fill = fill ?? Theme.Paper,
    };

    static Grid Box(params UIElement[] parts) => Box(Size, parts);

    static Grid Box(double size, params UIElement[] parts)
    {
        var g = new Grid
        {
            Width = size, Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        foreach (var p in parts) g.Children.Add(p);
        return g;
    }

    public static FrameworkElement Speaker(bool silent) => Box(
        Solid("M2.5,7.5 L6,7.5 L10,4 L10,16 L6,12.5 L2.5,12.5 Z"),
        Line(silent ? "M13,7.5 L17.5,12 M17.5,7.5 L13,12" : "M12.8,7.6 A3,3 0 0 1 12.8,12.4 M15,5.3 A6,6 0 0 1 15,14.7",
             silent ? Theme.Cert : Theme.Paper, 1.6));

    /// <summary>The CC box; an amber bar under it while subtitles are showing.</summary>
    public static FrameworkElement Captions(bool on)
    {
        var box = new Border
        {
            Width = 17, Height = 12, CornerRadius = new CornerRadius(2),
            BorderBrush = Theme.Paper, BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "CC", FontSize = 7, FontWeight = FontWeights.Bold, Foreground = Theme.Paper,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
            },
        };
        var bar = new Border
        {
            Height = 2, Width = 13, CornerRadius = new CornerRadius(1), Background = Theme.Lamp,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = on ? Visibility.Visible : Visibility.Hidden,
        };
        return Box(box, bar);
    }

    /// <summary>A window with a smaller one in its corner — the pop-out button.</summary>
    public static FrameworkElement PopOut() => Box(
        Line("M17,8.5 L17,4 L3,4 L3,16 L8.5,16", thickness: 1.6),
        Solid("M10.5,10.5 L17.5,10.5 L17.5,16 L10.5,16 Z"));

    public static FrameworkElement FullScreen() => Box(
        Line("M3,7.5 L3,3 L7.5,3 M12.5,3 L17,3 L17,7.5 M17,12.5 L17,17 L12.5,17 M7.5,17 L3,17 L3,12.5", thickness: 1.7));

    public static FrameworkElement Previous() => Box(Line("M5,4.5 L5,15.5", thickness: 2), Solid("M15.5,4.5 L7.5,10 L15.5,15.5 Z"));

    public static FrameworkElement Next() => Box(Line("M15,4.5 L15,15.5", thickness: 2), Solid("M4.5,4.5 L12.5,10 L4.5,15.5 Z"));

    public static FrameworkElement Minimise() => Box(Line("M4.5,10 L15.5,10"));

    public static FrameworkElement BackToApp() => Box(Line("M11.5,3.5 L16.5,3.5 L16.5,8.5 M16.5,3.5 L11,9 M8.5,16.5 L3.5,16.5 L3.5,11.5 M3.5,16.5 L9,11"));

    public static FrameworkElement Close() => Box(Line("M5,5 L15,15 M15,5 L5,15"));

    public static FrameworkElement Play(Brush? colour = null) => Box(Solid("M6,3.5 L16.5,10 L6,16.5 Z", colour));

    public static FrameworkElement Pause(Brush? colour = null) => Box(Solid("M5,4 L8.5,4 L8.5,16 L5,16 Z M11.5,4 L15,4 L15,16 L11.5,16 Z", colour));

    /// <summary>
    /// A circular arrow with "10" inside. Drawn around a circle centred in the
    /// square: forward runs clockwise and ends pointing right at the top, back
    /// is its mirror image.
    /// </summary>
    public static FrameworkElement Skip(bool forward, double size = 24)
    {
        // circle centre (12,12), radius 8, open between the top and 60° round
        var arc = forward
            ? "M18.93,8 A8,8 0 1 1 12,4 M12,4 L9.4,1.6 M12,4 L9.4,6.4"
            : "M5.07,8 A8,8 0 1 0 12,4 M12,4 L14.6,1.6 M12,4 L14.6,6.4";
        var ten = new TextBlock
        {
            Text = "10", FontSize = 7.5, FontWeight = FontWeights.Bold, Foreground = Theme.Paper,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0),
        };
        var drawing = Box(24, Line(arc, thickness: 1.7), ten);
        if (Math.Abs(size - 24) > 0.1) drawing.LayoutTransform = new ScaleTransform(size / 24, size / 24);
        return drawing;
    }
}
