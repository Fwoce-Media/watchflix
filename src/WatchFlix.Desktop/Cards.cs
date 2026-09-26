using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WatchFlix.Core.Data;
using WatchFlix.Core.Media;

namespace WatchFlix.Desktop;

/// <summary>Posters, wide resume cards, episode rows and portraits.</summary>
public static class Cards
{
    // The web page's measurements: every card in a row is 300px, grids share
    // the width out from a 168px minimum, 18px between everything.
    public const double PosterWidth = 300;
    public const double GridMinWidth = 168;
    public const double PeopleMinWidth = 132;
    public const double Gap = 18;
    public const double PosterStep = PosterWidth + Gap;
    public const double WideWidth = 300;
    public const double WideStep = WideWidth + Gap;

    static string PosterOf(Row item)
    {
        foreach (var key in new[] { "poster", "thumb", "backdrop" })
            if (item.Truthy(key)) return item.Str(key);
        return "";
    }

    static Border Art(string url, double width, double height, int decode)
    {
        var grid = new Grid();
        if (url.Length > 0) grid.Children.Add(Images.Lazy(url, decode));
        return new Border
        {
            Width = width,
            Height = height,
            Background = Theme.Rail,
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true,
            Child = grid,
        };
    }

    static TextBlock NoArt(string text)
    {
        var t = Ui.Text(text, 10.5, Theme.Dimmer, wrap: true, mono: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.VerticalAlignment = VerticalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        t.Margin = new Thickness(12);
        return t;
    }

    /// <summary>Keeps an element at a fixed shape whatever width it is given.</summary>
    static void Aspect(FrameworkElement element, double heightPerWidth) =>
        element.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
                element.Height = Math.Round(e.NewSize.Width * heightPerWidth);
        };

    static StackPanel CornerBadges()
    {
        return new StackPanel
        {
            Margin = new Thickness(0, 7, 7, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    static void AddBadge(StackPanel badges, Border b)
    {
        b.Margin = new Thickness(0, 0, 0, 4);
        b.HorizontalAlignment = HorizontalAlignment.Right;
        badges.Children.Add(b);
    }

    /// <summary>The line of mono facts under a card title.</summary>
    static WrapPanel Meta(IEnumerable<string> facts)
    {
        var meta = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var f in facts.Where(f => f.Length > 0))
            meta.Children.Add(Ui.Mono(f, Theme.Dimmer, 10.5).Margin(0, 0, 8, 0));
        return meta;
    }

    /// <summary>
    /// The card case every film and series sits in: shelf panel, rail edge,
    /// and the spine down its left side that lights amber on hover.
    /// </summary>
    static Button Case(Border art, UIElement body, Action click, string tip, out Grid inner)
    {
        var stack = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(art, Dock.Top);
        stack.Children.Add(art);
        stack.Children.Add(body);
        var spine = new Border { Width = 3, Background = Theme.Edge, HorizontalAlignment = HorizontalAlignment.Left };
        inner = new Grid();
        inner.Children.Add(stack);
        inner.Children.Add(spine);
        var card = new Border
        {
            Background = Theme.Shelf,
            BorderBrush = Theme.Rail,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Child = inner,
        };
        var lift = new TranslateTransform();
        card.RenderTransform = lift;
        var button = Ui.Bare(card, click, tip);
        button.VerticalAlignment = VerticalAlignment.Stretch;
        void Hover(bool on)
        {
            card.BorderBrush = on ? Theme.Edge : Theme.Rail;
            spine.Background = on ? Theme.Lamp : Theme.Edge;
            lift.BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(on ? -2 : 0, TimeSpan.FromMilliseconds(160)));
        }
        button.MouseEnter += (_, _) => Hover(true);
        button.MouseLeave += (_, _) => Hover(false);
        button.GotKeyboardFocus += (_, _) => Hover(true);
        button.LostKeyboardFocus += (_, _) => Hover(false);
        return button;
    }

    static TextBlock CardTitle(string text, int lines)
    {
        var title = Ui.Text(text, 13, Theme.Paper, FontWeights.Bold, wrap: lines > 1);
        title.LineHeight = 17;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.MaxHeight = 17 * lines;
        return title;
    }

    /// <summary>
    /// A short clip plays over the poster after a moment's hover. Built only
    /// when first wanted, and only one plays at a time.
    /// </summary>
    static MediaElement? _playing;

    static void AttachPreview(FrameworkElement host, Grid artGrid, string preview)
    {
        if (string.IsNullOrEmpty(preview)) return;
        var file = MediaUtil.LocalFileFor(preview);
        if (file == null) return;
        DispatcherTimer? timer = null;
        MediaElement? clip = null;
        host.MouseEnter += (_, _) =>
        {
            timer?.Stop();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            timer.Tick += (_, _) =>
            {
                timer?.Stop();
                try
                {
                    if (_playing != null && _playing != clip) { _playing.Stop(); _playing.Visibility = Visibility.Collapsed; }
                    if (clip == null)
                    {
                        clip = new MediaElement
                        {
                            LoadedBehavior = MediaState.Manual,
                            UnloadedBehavior = MediaState.Close,
                            IsMuted = true,
                            Stretch = Stretch.UniformToFill,
                            Source = new Uri(file),
                        };
                        clip.MediaEnded += (_, _) => { clip.Position = TimeSpan.Zero; clip.Play(); };
                        clip.MediaFailed += (_, _) => clip.Visibility = Visibility.Collapsed;
                        artGrid.Children.Add(clip);
                    }
                    clip.Visibility = Visibility.Visible;
                    clip.Play();
                    _playing = clip;
                }
                catch { }
            };
            timer.Start();
        };
        host.MouseLeave += (_, _) =>
        {
            timer?.Stop();
            if (clip != null)
            {
                clip.Pause();
                clip.Visibility = Visibility.Collapsed;
            }
        };
    }

    /// <summary>
    /// A poster for a film or series, built like the web card: artwork on top,
    /// title and facts on a panel beneath.
    /// </summary>
    public static FrameworkElement Poster(Row item, Action<Row> open, bool quality = true, double width = PosterWidth)
    {
        var url = PosterOf(item);
        var artGrid = new Grid();
        if (url.Length > 0) artGrid.Children.Add(Images.Lazy(url, 600));
        else artGrid.Children.Add(NoArt("no artwork\nscrape or add a poster"));
        var art = new Border { Background = Theme.Rail, ClipToBounds = true, Child = artGrid, Height = Math.Round(width * 1.5) };
        Aspect(art, 1.5);

        var badges = CornerBadges();
        if (item.Truthy("certificate")) AddBadge(badges, Ui.Badge(item.Str("certificate"), Theme.Cert));
        if (item.Double("rating") is > 0) AddBadge(badges, Ui.Badge(Ui.Rating(item.Double("rating")), Theme.Lamp));
        if (quality && item.Truthy("quality")) AddBadge(badges, Ui.Badge(item.Str("quality")));
        if (item.Truthy("unplayable")) AddBadge(badges, Ui.Badge("MISSING", Theme.Cert));
        artGrid.Children.Add(badges);

        var facts = new List<string>();
        if (item.Truthy("year")) facts.Add(item.Str("year"));
        if (item.Str("kind") == "show" && item.Long("season_count") is long n and > 0)
            facts.Add($"{n} season{(n == 1 ? "" : "s")}");
        else if (item.Double("duration") is > 0) facts.Add(Ui.Runtime(item.Double("duration")));
        var genres = Ui.Strings(item.Get("genres"));
        if (genres.Count > 0) facts.Add(genres[0]);

        var body = new Border { Padding = new Thickness(11, 10, 11, 12), Child = Ui.Column(CardTitle(item.Str("title"), 2), Meta(facts)) };
        var button = Case(art, body, () => open(item), item.Str("title"), out _);
        button.Width = width;
        button.Margin = new Thickness(0, 0, Gap, 0);
        AttachPreview(button, artGrid, item.Str("preview"));
        return button;
    }

    /// <summary>
    /// Cards in rows that fill the width: as many columns as fit at the
    /// minimum size, then every card stretched to share the space evenly.
    /// </summary>
    public static FrameworkElement FillGrid(IEnumerable<FrameworkElement> cards, double minWidth = GridMinWidth)
    {
        var wrap = new WrapPanel();
        foreach (var c in cards) wrap.Children.Add(c);
        void Fit(double available)
        {
            if (available <= 0) return;
            var columns = Math.Max(1, (int)Math.Floor((available + Gap) / (minWidth + Gap)));
            var width = Math.Floor((available - Gap * (columns - 1)) / columns) - 0.5;
            for (var i = 0; i < wrap.Children.Count; i++)
                if (wrap.Children[i] is FrameworkElement fe)
                {
                    fe.Width = width;
                    fe.Margin = new Thickness(0, 0, (i + 1) % columns == 0 ? 0 : Gap, Gap);
                }
        }
        var host = new Border { Child = wrap };
        host.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) Fit(e.NewSize.Width);
        };
        return host;
    }

    /// <summary>A wide card for Continue watching: where you got to, and a round remove button.</summary>
    public static FrameworkElement Resume(Row item, Action<Row> play, Action<Row> remove)
    {
        var url = "";
        foreach (var key in new[] { "thumb", "episode_still", "backdrop", "poster" })
            if (item.Truthy(key)) { url = item.Str(key); break; }
        var artGrid = new Grid();
        if (url.Length > 0) artGrid.Children.Add(Images.Lazy(url, 600));
        else artGrid.Children.Add(NoArt("no artwork"));
        var art = new Border { Background = Theme.Rail, ClipToBounds = true, Child = artGrid, Height = Math.Round(WideWidth * 9 / 16) };
        Aspect(art, 9.0 / 16);
        var badges = CornerBadges();
        if (item.Truthy("quality")) AddBadge(badges, Ui.Badge(item.Str("quality")));
        artGrid.Children.Add(badges);
        var bar = Ui.ProgressBar(item.Double("progress") ?? 0);
        bar.VerticalAlignment = VerticalAlignment.Bottom;
        artGrid.Children.Add(bar);

        var left = Ui.Runtime(item.Double("remaining"));
        var body = new Border
        {
            Padding = new Thickness(11, 10, 11, 12),
            Child = Ui.Column(CardTitle(item.Str("title"), 1), Meta(new[] { item.Str("subtitle"), left.Length > 0 ? left + " left" : "" })),
        };
        var card = Case(art, body, () => play(item), "Resume " + item.Str("title"), out _);

        var cross = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3.5,3.5 L12.5,12.5 M12.5,3.5 L3.5,12.5"),
            Stroke = Theme.Dim, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 16, Height = 16, Stretch = Stretch.None,
            LayoutTransform = new ScaleTransform(11 / 16.0, 11 / 16.0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var ring = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x13, 0x18)),
            BorderBrush = Theme.Edge, BorderThickness = new Thickness(1),
            Child = cross,
        };
        var x = Ui.Bare(ring, () => remove(item), "Remove from Continue watching");
        x.HorizontalAlignment = HorizontalAlignment.Left;
        x.VerticalAlignment = VerticalAlignment.Top;
        x.Margin = new Thickness(10, 7, 0, 0);
        x.Visibility = Visibility.Hidden;
        x.MouseEnter += (_, _) => { cross.Stroke = Theme.Cert; ring.BorderBrush = Theme.Cert; };
        x.MouseLeave += (_, _) => { cross.Stroke = Theme.Dim; ring.BorderBrush = Theme.Edge; };

        var host = new Grid { Width = WideWidth, Margin = new Thickness(0, 0, Gap, 0), VerticalAlignment = VerticalAlignment.Stretch };
        host.Children.Add(card);
        host.Children.Add(x);
        host.MouseEnter += (_, _) => x.Visibility = Visibility.Visible;
        host.MouseLeave += (_, _) => x.Visibility = Visibility.Hidden;
        return host;
    }

    /// <summary>One episode on a series page: a still, what it is, and whether it has been seen.</summary>
    public static FrameworkElement Episode(Row ep, Action<Row> play)
    {
        var pct = ep.Double("position") is > 0 && ep.Double("duration") is > 0
            ? ep.Double("position")!.Value / ep.Double("duration")!.Value : 0;
        var art = ep.Truthy("still") ? ep.Str("still") : ep.Str("thumb");
        var artBorder = Art(art, 190, 107, 400);
        artBorder.VerticalAlignment = VerticalAlignment.Top;
        var artGrid = (Grid)artBorder.Child;
        var num = new Border
        {
            Background = Theme.Vault,
            CornerRadius = new CornerRadius(0, 2, 0, 0),
            Padding = new Thickness(7, 2, 7, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = Ui.Text($"S{ep.Long("season") ?? 0:00}E{ep.Long("episode") ?? 0:00}", 10.8, Theme.Lamp, FontWeights.Bold, mono: true),
        };
        artGrid.Children.Add(num);
        if (pct > 0.01)
        {
            var bar = Ui.ProgressBar(pct);
            bar.VerticalAlignment = VerticalAlignment.Bottom;
            artGrid.Children.Add(bar);
        }

        var facts = new List<string> { ep.Str("quality"), Ui.Runtime(ep.Double("duration")), ep.Str("air_date") };
        if (ep.Double("rating") is > 0) facts.Add("★ " + Ui.Rating(ep.Double("rating")));
        if (ep.Truthy("missing")) facts.Add("MISSING");
        var watched = ep.Truthy("completed");
        TextBlock desc;
        if (ep.Truthy("overview"))
        {
            desc = Ui.Text(ep.Str("overview"), 13, Theme.Dim, wrap: true);
            desc.LineHeight = 19.5;
            desc.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            desc.MaxHeight = 19.5 * 3;
            desc.TextTrimming = TextTrimming.WordEllipsis;
        }
        else desc = Ui.Text(ep.Str("filename"), 13, Theme.Dimmer);
        var text = Ui.Column(
            Ui.Text(ep.Str("title"), 15, watched ? Theme.Dim : Theme.Paper, FontWeights.Bold, margin: new Thickness(0, 0, 0, 4)),
            desc,
            Ui.Mono(string.Join("  ·  ", facts.Where(f => f.Length > 0)), Theme.Dimmer).Margin(0, 6, 0, 0));
        var status = watched ? "Watched" : pct > 0.01 ? "In progress" : "Unplayed";
        var side = Ui.Badge(status);
        side.Margin = new Thickness(0);
        side.VerticalAlignment = VerticalAlignment.Top;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        text.Margin = new Thickness(16, 0, 16, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(side, 2);
        grid.Children.Add(artBorder);
        grid.Children.Add(text);
        grid.Children.Add(side);
        var row = new Border
        {
            Background = Theme.Shelf,
            BorderBrush = Theme.Rail,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12),
            Child = grid,
        };
        var button = Ui.Bare(row, () => play(ep), "Play " + ep.Str("title"));
        button.Margin = new Thickness(0, 0, 0, 10);
        button.MouseEnter += (_, _) => row.BorderBrush = Theme.Edge;
        button.MouseLeave += (_, _) => row.BorderBrush = Theme.Rail;
        return button;
    }

    /// <summary>A portrait for the cast lists and the Actors page; sits in a FillGrid.</summary>
    public static FrameworkElement Person(Row p, string subtitle, Action<Row> open)
    {
        var photo = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = Theme.Rail,
            BorderBrush = Theme.Rail,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 0, 8),
            Height = 176,
        };
        Aspect(photo, 4.0 / 3);
        if (p.Truthy("photo")) photo.Child = Images.Lazy(p.Str("photo"), 300);
        else
        {
            var initials = Ui.Text(Ui.Initials(p.Str("name")), 21, Theme.Dimmer, mono: true);
            initials.HorizontalAlignment = HorizontalAlignment.Center;
            initials.VerticalAlignment = VerticalAlignment.Center;
            photo.Child = initials;
        }
        var name = Ui.Text(p.Str("name"), 12.75, Theme.Paper, FontWeights.Bold, wrap: true);
        name.LineHeight = 16;
        var role = Ui.Text(subtitle, 10.5, Theme.Dimmer, wrap: true, mono: true, margin: new Thickness(0, 3, 0, 0));
        var b = Ui.Bare(Ui.Column(photo, name, role), () => open(p), p.Str("name"));
        b.VerticalAlignment = VerticalAlignment.Top;
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        b.MouseEnter += (_, _) => photo.BorderBrush = Theme.LampDim;
        b.MouseLeave += (_, _) => photo.BorderBrush = Theme.Rail;
        return b;
    }

    public static FrameworkElement People(IEnumerable<FrameworkElement> portraits) => FillGrid(portraits, PeopleMinWidth);
}
