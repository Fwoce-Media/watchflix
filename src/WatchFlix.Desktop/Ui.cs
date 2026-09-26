using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WatchFlix.Core.Data;
using WatchFlix.Core.Media;
using WatchFlix.Core.Providers;

namespace WatchFlix.Desktop;

/// <summary>Small builders so every screen is put together the same way.</summary>
public static class Ui
{
    public static TextBlock Text(string text, double size = 14, Brush? color = null, FontWeight? weight = null,
                                 bool wrap = false, bool mono = false, Thickness? margin = null)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = color ?? Theme.Paper,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            Margin = margin ?? new Thickness(0),
        };
        if (mono) t.FontFamily = Theme.Mono;
        return t;
    }

    public static TextBlock Heading(string text, double size = 22) =>
        Text(text, size, Theme.Paper, FontWeights.SemiBold, margin: new Thickness(0, 0, 0, 12));

    public static TextBlock Mono(string text, Brush? color = null, double size = 11.7) =>
        Text(text, size, color ?? Theme.Dim, mono: true);

    public static Button Button(string label, Action onClick, bool primary = false, string? tip = null)
    {
        var b = new Button { Content = label };
        if (primary && Theme.Style("Primary") is { } style) b.Style = style;
        if (tip != null) b.ToolTip = tip;
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Bare(UIElement content, Action onClick, string? tip = null)
    {
        var b = new Button { Content = content };
        if (Theme.Style("Bare") is { } style) b.Style = style;
        else
        {
            b.Background = Theme.Clear;
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(0);
        }
        if (tip != null) b.ToolTip = tip;
        b.Click += (_, _) => onClick();
        return b;
    }

    // ------------------------------------------------------ the web type scale
    // Sizes are the web page's rem values at its 15px base.

    /// <summary>Upper-case, letter-spaced text: row and section headings, kickers, labels.</summary>
    public static Tracked Caps(string text, double size, Brush color, double em, bool mono = false, FontWeight? weight = null) =>
        new Tracked { Family = mono ? Theme.Mono : Theme.Sans, Size = size, Em = em }
            .Add(text.ToUpperInvariant(), color, weight ?? FontWeights.Normal);

    /// <summary>"CONTINUE WATCHING" — the heading over a row of posters.</summary>
    public static Tracked RowHeading(string text) => Caps(text, 12.3, Theme.Dim, 0.12, weight: FontWeights.ExtraBold);

    /// <summary>A section heading with a hairline running to the right edge.</summary>
    public static FrameworkElement SectionHeading(string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = RowHeading(text);
        label.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(label);
        var rule = new Border { Height = 1, Background = Theme.Rail, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);
        return grid;
    }

    /// <summary>A section: its heading, then its contents, 36px clear of the next.</summary>
    public static StackPanel Section(string title, params UIElement[] children)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 36) };
        s.Children.Add(SectionHeading(title));
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    /// <summary>MOVIES  1,204 titles  ……  [controls]</summary>
    public static FrameworkElement PageHead(string title, string? count = null, UIElement? controls = null)
    {
        var dock = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 20) };
        if (controls != null)
        {
            DockPanel.SetDock(controls, Dock.Right);
            if (controls is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Bottom;
            dock.Children.Add(controls);
        }
        var h1 = new Tracked { Size = 22.5, Em = -0.03 }.Add(title.ToUpperInvariant(), Theme.Paper, FontWeights.ExtraBold);
        h1.VerticalAlignment = VerticalAlignment.Bottom;
        dock.Children.Add(h1);
        if (count != null)
        {
            var c = Mono(count, Theme.Dimmer);
            c.VerticalAlignment = VerticalAlignment.Bottom;
            c.Margin = new Thickness(18, 0, 0, 4);
            dock.Children.Add(c);
        }
        return dock;
    }

    /// <summary>The small mono caption over a form field.</summary>
    public static Tracked FieldLabel(string text)
    {
        var t = Caps(text, 10.8, Theme.Dim, 0.08, mono: true);
        t.Margin = new Thickness(0, 0, 0, 5);
        t.HorizontalAlignment = HorizontalAlignment.Left;
        return t;
    }

    /// <summary>Label and control — one form field.</summary>
    public static StackPanel Field(string label, UIElement input)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        s.Children.Add(FieldLabel(label));
        s.Children.Add(input);
        return s;
    }

    /// <summary>Fields side by side, sharing the width evenly.</summary>
    public static Grid FieldRow(params UIElement[] fields)
    {
        var grid = new Grid();
        for (var i = 0; i < fields.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (fields[i] is FrameworkElement fe) fe.Margin = new Thickness(i == 0 ? 0 : 6, fe.Margin.Top, i == fields.Length - 1 ? 0 : 6, fe.Margin.Bottom);
            Grid.SetColumn(fields[i], i);
            grid.Children.Add(fields[i]);
        }
        return grid;
    }

    /// <summary>A row of buttons ten pixels apart that wraps when the window is narrow.</summary>
    public static WrapPanel Actions(params UIElement[] buttons)
    {
        var w = new WrapPanel();
        foreach (var b in buttons)
        {
            if (b is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 10, 10);
            w.Children.Add(b);
        }
        return w;
    }

    /// <summary>An amber underlined text link.</summary>
    public static Button Link(string label, Action onClick, Brush? color = null)
    {
        var t = Text(label, 12, color ?? Theme.Lamp);
        t.TextDecorations = TextDecorations.Underline;
        var b = Bare(t, onClick);
        b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    /// <summary>A dark box with a rail edge — folder paths, providers, addresses.</summary>
    public static Border Box(UIElement child, double bottom = 8) => new()
    {
        Background = Theme.Vault,
        BorderBrush = Theme.Rail,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(12, 10, 12, 10),
        Margin = new Thickness(0, 0, 0, bottom),
        Child = child,
    };

    public static System.Windows.Shapes.Ellipse Dot(Brush fill) => new()
    {
        Width = 8, Height = 8, Fill = fill, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
    };

    static readonly Brush BadgeFill = Freeze(new SolidColorBrush(Color.FromArgb(0xDB, 0x10, 0x13, 0x18)));
    static readonly Brush CertEdge = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xC7, 0x57, 0x3F)));
    public static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x17, 0x11, 0x02)));
    public static readonly Brush Body = Freeze(new SolidColorBrush(Color.FromRgb(0xCF, 0xCB, 0xC2)));

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>
    /// A mono tag. Pass Theme.Cert for a certificate or warning, Theme.Lamp for
    /// a rating or something active; anything else is a plain tag.
    /// </summary>
    public static Border Badge(string text, Brush? fill = null, Brush? fg = null)
    {
        var cert = fill == Theme.Cert || fg == Theme.Cert;
        var lamp = fill == Theme.Lamp || fg == Theme.Lamp;
        var colour = cert ? Theme.Cert : lamp ? Theme.Lamp : fg == Theme.Good ? Theme.Good : Theme.Paper;
        return new Border
        {
            Background = BadgeFill,
            BorderBrush = cert ? CertEdge : Theme.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(text, 9.9, colour, FontWeights.SemiBold, mono: true),
        };
    }

    /// <summary>A genre as a small mono link: lights amber on hover.</summary>
    public static Button GenreLink(string text, Action onClick)
    {
        var label = Text(text, 10.8, Theme.Dim, mono: true);
        var border = new Border
        {
            BorderBrush = Theme.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(8, 3, 8, 3),
            Background = Theme.Clear,
            Child = label,
        };
        var b = Bare(border, onClick, $"Every {text.ToLowerInvariant()} title");
        b.Margin = new Thickness(0, 0, 6, 6);
        b.MouseEnter += (_, _) => { label.Foreground = Theme.Lamp; border.BorderBrush = Theme.LampDim; };
        b.MouseLeave += (_, _) => { label.Foreground = Theme.Dim; border.BorderBrush = Theme.Edge; };
        return b;
    }

    public static WrapPanel GenreLinks(IEnumerable<string> genres, Action<string> open)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        foreach (var g in genres)
        {
            var name = g;
            w.Children.Add(GenreLink(name, () => open(name)));
        }
        return w;
    }

    /// <summary>A genre filter: square-cornered, lit amber when chosen, with its count in mono.</summary>
    public static Button Chip(string text, bool on, Action onClick, long? count = null)
    {
        var label = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = on ? Ink : Theme.Dim };
        label.Inlines.Add(new System.Windows.Documents.Run(text));
        if (count != null)
            label.Inlines.Add(new System.Windows.Documents.Run("  " + count.Value.ToString(CultureInfo.InvariantCulture))
            {
                FontFamily = Theme.Mono, FontSize = 10.8, FontWeight = FontWeights.Normal,
                Foreground = on ? Ink : Theme.Dimmer,
            });
        var border = new Border
        {
            Background = on ? Theme.Lamp : Theme.Clear,
            BorderBrush = on ? Theme.Lamp : Theme.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(11, 5, 11, 5),
            Child = label,
        };
        var b = Bare(border, onClick);
        b.Margin = new Thickness(0, 0, 6, 6);
        if (!on)
        {
            b.MouseEnter += (_, _) => label.Foreground = Theme.Paper;
            b.MouseLeave += (_, _) => label.Foreground = Theme.Dim;
        }
        return b;
    }

    /// <summary>Both options visible, the active one lit — so there is nothing to infer.</summary>
    public static Border Segmented(int active, params (string Label, Action Pick)[] options)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < options.Length; i++)
        {
            var on = i == active;
            var label = Text(options[i].Label, 12.75, on ? Ink : Theme.Dim, FontWeights.SemiBold);
            var cell = new Border
            {
                Background = on ? Theme.Lamp : Theme.Clear,
                BorderBrush = Theme.Edge,
                BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                Padding = new Thickness(14, 7, 14, 7),
                Child = label,
            };
            var pick = options[i].Pick;
            var b = Bare(cell, () => { if (!on) pick(); });
            if (!on)
            {
                b.MouseEnter += (_, _) => { label.Foreground = Theme.Paper; cell.Background = Theme.Shelf; };
                b.MouseLeave += (_, _) => { label.Foreground = Theme.Dim; cell.Background = Theme.Clear; };
            }
            row.Children.Add(b);
        }
        return new Border
        {
            BorderBrush = Theme.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Child = row,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>A sort or filter drop-down, sized like the web page's.</summary>
    public static ComboBox Select(IEnumerable<(string Key, string Label)> options, string current, Action<string> picked, double width = 170)
    {
        var box = new ComboBox { Width = width, Height = 32 };
        foreach (var (key, label) in options)
        {
            box.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            if (key == current) box.SelectedIndex = box.Items.Count - 1;
        }
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string key }) picked(key);
        };
        return box;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var s = new StackPanel();
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static T Margin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }

    /// <summary>
    /// A scrolling page with the web view's margins: 24px either side, capped
    /// at 1680px wide and centred on very wide screens.
    /// </summary>
    public static ScrollViewer Page(UIElement content, double pad = 24) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
        Content = new Border
        {
            Padding = new Thickness(pad, 26, pad, 70),
            MaxWidth = 1680 + pad * 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content,
        },
    };

    /// <summary>A scrolling page whose top runs edge to edge (the billboard, a title's hero).</summary>
    public static ScrollViewer Bleed(UIElement top, UIElement rest)
    {
        var body = new Border { Padding = new Thickness(24, 0, 24, 70), MaxWidth = 1728, Child = rest };
        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(body);
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = stack,
        };
    }

    /// <summary>The dashed box shown when there is nothing to list.</summary>
    public static FrameworkElement Empty(string title, string body, UIElement? action = null)
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var h = new Tracked { Size = 16.5, Em = -0.02 }.Add(title.ToUpperInvariant(), Theme.Paper, FontWeights.Bold);
        h.HorizontalAlignment = HorizontalAlignment.Center;
        h.Margin = new Thickness(0, 0, 0, 8);
        s.Children.Add(h);
        if (body.Length > 0)
        {
            var b = Text(body, 14, Theme.Dim, wrap: true);
            b.TextAlignment = TextAlignment.Center;
            b.MaxWidth = 440;
            s.Children.Add(b);
        }
        if (action is FrameworkElement fe)
        {
            fe.Margin = new Thickness(0, 18, 0, 0);
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            s.Children.Add(fe);
        }
        var grid = new Grid { MaxWidth = 620, Margin = new Thickness(0, 40, 0, 40) };
        grid.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Stroke = Theme.Edge, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
            RadiusX = 3, RadiusY = 3, SnapsToDevicePixels = true,
        });
        grid.Children.Add(new Border { Padding = new Thickness(28, 44, 28, 44), Child = s });
        return grid;
    }

    public static FrameworkElement Loading(string text = "Loading…") =>
        Mono(text, Theme.Dim, 12).Margin(24, 40, 0, 0);

    public static FrameworkElement ProgressBar(double fraction, double height = 3)
    {
        var grid = new Grid { Height = height, Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(fraction, 0, 1), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - Math.Clamp(fraction, 0, 1), GridUnitType.Star) });
        var fill = new Border { Background = Theme.Lamp };
        grid.Children.Add(fill);
        return grid;
    }

    /// <summary>
    /// Paints a picture over an element like CSS background-size: cover, with
    /// the crop anchored at a point (0.5, 0.22 is "center 22%"). Being a
    /// background, it never changes the element's size.
    /// </summary>
    public static void Cover(Border host, string? url, double opacity = 1, double anchorX = 0.5, double anchorY = 0.5, int decode = 1920)
    {
        if (string.IsNullOrEmpty(url)) return;
        _ = Images.LoadAsync(url, decode).ContinueWith(t =>
        {
            var bmp = t.Result;
            if (bmp == null || bmp.PixelWidth == 0 || bmp.PixelHeight == 0) return;
            var brush = new ImageBrush(bmp)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Opacity = opacity,
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            void Fit()
            {
                if (host.ActualWidth <= 0 || host.ActualHeight <= 0) return;
                var box = host.ActualWidth / host.ActualHeight;
                var pic = (double)bmp.PixelWidth / bmp.PixelHeight;
                if (pic > box)
                {
                    var w = box / pic;
                    brush.Viewbox = new Rect((1 - w) * anchorX, 0, w, 1);
                }
                else
                {
                    var h = pic / box;
                    brush.Viewbox = new Rect(0, (1 - h) * anchorY, 1, h);
                }
            }
            Fit();
            host.SizeChanged += (_, _) => Fit();
            host.Background = brush;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// A small scroller inside a page (a long synopsis): it takes the wheel
    /// while it can move, then hands it to the page.
    /// </summary>
    public static void ChainWheel(ScrollViewer inner)
    {
        inner.PreviewMouseWheel += (_, e) =>
        {
            var up = e.Delta > 0;
            var atEnd = up ? inner.VerticalOffset <= 0 : inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
            if (!atEnd) return;
            e.Handled = true;
            var parent = VisualTreeHelper.GetParent(inner) as UIElement;
            parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = inner,
            });
        };
    }

    // ---------------------------------------------------------------- formats
    public static string Runtime(double? seconds)
    {
        if (seconds is not > 0) return "";
        var total = (int)Math.Round(seconds.Value / 60);
        var h = total / 60;
        var m = total % 60;
        return h > 0 ? $"{h}h {m:00}m" : $"{m}m";
    }

    public static string Minutes(long? minutes) => minutes is > 0 ? Runtime(minutes.Value * 60) : "";

    public static string Clock(double seconds)
    {
        var s = (long)Math.Max(0, Math.Floor(seconds));
        var h = s / 3600;
        var m = s % 3600 / 60;
        var sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m}:{sec:00}";
    }

    public static string Bytes(double n)
    {
        if (n <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = Math.Min((int)Math.Floor(Math.Log(n) / Math.Log(1024)), units.Length - 1);
        return (n / Math.Pow(1024, i)).ToString(i > 0 ? "F1" : "F0", CultureInfo.InvariantCulture) + " " + units[i];
    }

    public static string Rating(double? rating) =>
        rating is > 0 ? rating.Value.ToString("F1", CultureInfo.InvariantCulture) : "";

    public static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public static List<string> Strings(object? value) =>
        value is IEnumerable<string> list ? list.ToList() : new List<string>();

    public static List<Row> Rows(object? value) =>
        value is IEnumerable<Row> list ? list.ToList() : new List<Row>();

    /// <summary>
    /// A horizontal scroller inside a vertical page must let the mouse wheel
    /// through, or the page stops scrolling whenever the pointer is over a row.
    /// Shift+wheel still scrolls the row sideways.
    /// </summary>
    public static void PassWheelToParent(ScrollViewer inner)
    {
        inner.PreviewMouseWheel += (sender, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                inner.ScrollToHorizontalOffset(inner.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }
            e.Handled = true;
            var parent = VisualTreeHelper.GetParent(inner) as UIElement;
            parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = inner,
            });
        };
    }

    /// <summary>
    /// A row that scrolls sideways, as the web strip does: a thin scroll bar
    /// underneath, and round arrows over faded ends that appear on hover and
    /// never at a limit. The arrows move a whole number of cards.
    /// </summary>
    public static FrameworkElement Strip(IEnumerable<UIElement> items, double cardStep)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in items) panel.Children.Add(item);
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
            Focusable = false,
            CanContentScroll = false,
        };
        PassWheelToParent(scroller);

        Button Arrow(string glyph, int direction)
        {
            var fade = new LinearGradientBrush
            {
                StartPoint = new Point(direction < 0 ? 0 : 1, 0),
                EndPoint = new Point(direction < 0 ? 1 : 0, 0),
                GradientStops = new GradientStopCollection
                {
                    new(Theme.VaultC, 0.15),
                    new(Color.FromArgb(0xD1, 0x10, 0x13, 0x18), 0.55),
                    new(Color.FromArgb(0x00, 0x10, 0x13, 0x18), 1),
                },
            };
            var mark = Text(glyph, 14, Theme.Paper);
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.VerticalAlignment = VerticalAlignment.Center;
            mark.Margin = new Thickness(0, -2, 0, 0);
            var circle = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
                Background = Theme.Shelf, BorderBrush = Theme.Edge, BorderThickness = new Thickness(1),
                Child = mark,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };
            var face = new Grid { Background = fade };
            face.Children.Add(circle);
            var b = Bare(face, () =>
            {
                var steps = Math.Max(1, Math.Floor(scroller.ViewportWidth / cardStep));
                var target = scroller.HorizontalOffset + direction * steps * cardStep;
                scroller.ScrollToHorizontalOffset(Math.Max(0, Math.Round(target / cardStep) * cardStep));
            });
            b.Width = 52;
            b.Margin = new Thickness(0, 0, 0, 22);
            b.HorizontalAlignment = direction < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            b.VerticalAlignment = VerticalAlignment.Stretch;
            b.Visibility = Visibility.Collapsed;
            b.Focusable = false;
            b.MouseEnter += (_, _) =>
            {
                circle.Background = Theme.Lamp; circle.BorderBrush = Theme.Lamp; mark.Foreground = Ink;
                ((ScaleTransform)circle.RenderTransform).ScaleX = ((ScaleTransform)circle.RenderTransform).ScaleY = 1.08;
            };
            b.MouseLeave += (_, _) =>
            {
                circle.Background = Theme.Shelf; circle.BorderBrush = Theme.Edge; mark.Foreground = Theme.Paper;
                ((ScaleTransform)circle.RenderTransform).ScaleX = ((ScaleTransform)circle.RenderTransform).ScaleY = 1;
            };
            return b;
        }
        var prev = Arrow("❮", -1);
        var next = Arrow("❯", 1);
        var grid = new Grid();
        grid.Children.Add(scroller);
        grid.Children.Add(prev);
        grid.Children.Add(next);
        void Update()
        {
            var slack = scroller.ExtentWidth - scroller.ViewportWidth;
            var hover = grid.IsMouseOver;
            prev.Visibility = hover && slack > 4 && scroller.HorizontalOffset > 2 ? Visibility.Visible : Visibility.Collapsed;
            next.Visibility = hover && slack > 4 && scroller.HorizontalOffset < slack - 2 ? Visibility.Visible : Visibility.Collapsed;
        }
        scroller.ScrollChanged += (_, _) => Update();
        grid.MouseEnter += (_, _) => Update();
        grid.MouseLeave += (_, _) => Update();
        return grid;
    }
}

/// <summary>
/// Text with letter-spacing, which WPF's own text cannot do: each character is
/// set on its own and the gap between them widened (or tightened) by a
/// fraction of the type size — the web page's letter-spacing in em.
/// </summary>
public sealed class Tracked : FrameworkElement
{
    readonly List<(string Text, Brush Brush, FontWeight Weight)> _parts = new();
    readonly List<(FormattedText Glyph, double X)> _laid = new();
    double _width, _height, _baseline;

    public FontFamily Family { get; set; } = Theme.Sans;
    public double Size { get; set; } = 12;
    public double Em { get; set; } = 0.1;
    public FontStyle Slant { get; set; } = FontStyles.Normal;

    public Tracked Add(string text, Brush brush, FontWeight? weight = null)
    {
        _parts.Add((text, brush, weight ?? FontWeights.Normal));
        InvalidateMeasure();
        InvalidateVisual();
        return this;
    }

    public void Set(string text, Brush brush, FontWeight? weight = null)
    {
        _parts.Clear();
        Add(text, brush, weight);
    }

    void Layout()
    {
        _laid.Clear();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double x = 0, h = 0, b = 0;
        var gap = Size * Em;
        var first = true;
        foreach (var (text, brush, weight) in _parts)
        {
            var face = new Typeface(Family, Slant, weight, FontStretches.Normal);
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext())
            {
                var ch = e.GetTextElement();
                if (!first) x += gap;
                first = false;
                var ft = new FormattedText(ch, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Size, brush, dpi);
                _laid.Add((ft, x));
                x += ft.WidthIncludingTrailingWhitespace;
                h = Math.Max(h, ft.Height);
                b = Math.Max(b, ft.Baseline);
            }
        }
        _width = Math.Max(0, x);
        _height = h > 0 ? h : Size * 1.3;
        _baseline = b;
    }

    protected override Size MeasureOverride(Size available)
    {
        Layout();
        return new Size(_width, _height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var (glyph, x) in _laid)
            dc.DrawText(glyph, new Point(x, _baseline - glyph.Baseline));
    }
}

/// <summary>
/// Artwork, loaded off the UI thread and kept in memory at the size it is shown,
/// so scrolling back over a row costs nothing.
/// </summary>
public static class Images
{
    static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new();
    static readonly System.Threading.SemaphoreSlim Throttle = new(6);

    public static async Task<BitmapSource?> LoadAsync(string? url, int decodeWidth)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var key = $"{decodeWidth}|{url}";
        if (Cache.TryGetValue(key, out var hit)) return hit;
        await Throttle.WaitAsync();
        try
        {
            var image = await Task.Run(() => Decode(url, decodeWidth));
            Cache[key] = image;
            return image;
        }
        finally
        {
            Throttle.Release();
        }
    }

    static BitmapSource? Decode(string url, int decodeWidth)
    {
        try
        {
            byte[]? bytes = null;
            var local = MediaUtil.LocalFileFor(url);
            if (local != null) bytes = File.ReadAllBytes(local);
            else if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) bytes = Http.GetBytes(url);
            else if (File.Exists(url)) bytes = File.ReadAllBytes(url);
            if (bytes == null || bytes.Length < 64) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Forget everything held, after artwork has been replaced.</summary>
    public static void Clear() => Cache.Clear();

    /// <summary>An Image that fills itself in when its picture arrives.</summary>
    public static Image Lazy(string? url, int decodeWidth, Stretch stretch = Stretch.UniformToFill)
    {
        var image = new Image { Stretch = stretch, Opacity = 0 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        if (!string.IsNullOrEmpty(url))
            _ = Fill(image, url, decodeWidth);
        return image;
    }

    static async Task Fill(Image image, string url, int width)
    {
        var bmp = await LoadAsync(url, width);
        if (bmp == null) return;
        image.Source = bmp;
        image.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }
}
