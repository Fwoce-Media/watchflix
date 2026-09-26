using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WatchFlix.Core;
using WatchFlix.Core.Data;
using WatchFlix.Core.Library;

namespace WatchFlix.Desktop;

/// <summary>The screens. Each is built fresh from the library whenever it is shown.</summary>
public static partial class Pages
{
    public static async Task<FrameworkElement> Build(MainWindow win, Location where) => where.Kind switch
    {
        "home" => await Home(win),
        "movies" => await Library(win, "movie"),
        "shows" => await Library(win, "show"),
        "search" => await Search(win, where.Query),
        "movie" or "show" => await Detail(win, where),
        "actors" => await Actors(win),
        "person" => await Person(win, where),
        "settings" => SettingsPage.Build(win),
        _ => await Home(win),
    };

    static void Open(MainWindow win, Row item)
    {
        var kind = item.Str("kind") == "show" ? "show" : "movie";
        var id = item.Long("id") ?? item.Long("media_id") ?? 0;
        win.Navigate(new Location(kind, id) { Title = item.Str("title") });
    }

    static void RemoveResume(MainWindow win, Row item)
    {
        Catalog.DropFromContinue(item.Long("file_id") ?? 0);
        win.Refresh();
    }

    static FrameworkElement Shelf(MainWindow win, string title, List<Row> items, bool resume, Action? seeAll = null)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
        var h2 = Ui.RowHeading(title);
        h2.VerticalAlignment = VerticalAlignment.Bottom;
        head.Children.Add(h2);
        if (seeAll != null)
        {
            var more = Ui.Link("See all", seeAll);
            DockPanel.SetDock(more, Dock.Right);
            head.Children.Add(more);
        }
        var cards = items.Select(i => resume
            ? Cards.Resume(i, r => win.Play(r.Long("file_id") ?? 0, r.Double("position") ?? 0), r => RemoveResume(win, r))
            : (UIElement)Cards.Poster(i, r => Open(win, r)));
        var strip = Ui.Strip(cards, resume ? Cards.WideStep : Cards.PosterStep);
        return Ui.Column(head, strip).Margin(0, 0, 0, 30);
    }

    static FrameworkElement PosterGrid(MainWindow win, IEnumerable<Row> items) =>
        Cards.FillGrid(items.Select(item => Cards.Poster(item, r => Open(win, r))));

    static FrameworkElement PeopleGrid(MainWindow win, IEnumerable<(Row Person, string Role)> people) =>
        Cards.People(people.Select(p => Cards.Person(p.Person, p.Role,
            r => win.Navigate(new Location("person", r.Long("id") ?? 0) { Title = r.Str("name") }))));

    // ------------------------------------------------------------------ home
    static async Task<FrameworkElement> Home(MainWindow win)
    {
        var (hero, rows) = await Task.Run(Catalog.FrontPage);
        if (rows.Count == 0)
            return Ui.Page(Ui.Empty("Nothing here yet", "",
                Ui.Button("Open Settings", () => win.Navigate(new Location("settings")), primary: true)));

        var slides = await Task.Run(() => Catalog.Spotlight());
        if (slides.Count == 0 && hero != null) slides.Add(hero);

        var shelves = new StackPanel { Margin = new Thickness(0, slides.Count > 0 ? 14 : 26, 0, 0) };
        foreach (var row in rows)
            shelves.Children.Add(Shelf(win, row.Title, row.Items, row.Kind == "resume"));
        return Ui.Bleed(slides.Count > 0 ? Billboard(win, slides) : new Border(), shelves);
    }

    const double SlideSeconds = 5;

    /// <summary>
    /// The top of the home page: the two series and two films you are
    /// watching, taking turns every five seconds. The artwork does the work,
    /// so the panel is mostly a gradient that lets the picture show through
    /// and still leaves the words readable over any part of it.
    /// </summary>
    static FrameworkElement Billboard(MainWindow win, List<Row> slides)
    {
        var grid = new Grid { Height = 520, ClipToBounds = true, Background = Theme.Vault };
        var arts = new List<Border>();
        var texts = new List<FrameworkElement>();
        foreach (var slide in slides)
        {
            var art = new Border { Opacity = 0 };
            Ui.Cover(art, slide.Str("backdrop"), anchorY: 0.22);
            arts.Add(art);
            grid.Children.Add(art);
        }
        // along the bottom, so the text sits on something solid
        grid.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 1), EndPoint = new Point(0, 0),
                GradientStops = new GradientStopCollection
                {
                    new(Theme.VaultC, 0.02),
                    new(Color.FromArgb(0xB8, 0x10, 0x13, 0x18), 0.38),
                    new(Color.FromArgb(0x00, 0x10, 0x13, 0x18), 0.78),
                },
            },
        });
        // from the left, so the words are not fighting the picture
        grid.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0xEB, 0x10, 0x13, 0x18), 0),
                    new(Color.FromArgb(0x73, 0x10, 0x13, 0x18), 0.46),
                    new(Color.FromArgb(0x00, 0x10, 0x13, 0x18), 0.82),
                },
            },
        });
        grid.Children.Add(new Border { BorderBrush = Theme.Rail, BorderThickness = new Thickness(0, 0, 0, 1) });
        foreach (var slide in slides)
        {
            var text = BillboardText(win, slide);
            text.Opacity = 0;
            text.IsHitTestVisible = false;
            texts.Add(text);
            grid.Children.Add(text);
        }

        // one short bar per slide, bottom right; the lit one is showing
        var bars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 24, 40),
            Visibility = slides.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
        };
        grid.Children.Add(bars);

        var current = -1;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(SlideSeconds) };
        void Fade(UIElement e, double to) =>
            e.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(700)));
        void Show(int index)
        {
            if (index == current) return;
            for (var i = 0; i < slides.Count; i++)
            {
                var on = i == index;
                Fade(arts[i], on ? 1 : 0);
                Fade(texts[i], on ? 1 : 0);
                texts[i].IsHitTestVisible = on;
                if (bars.Children[i] is Button { Content: Border bar })
                    bar.Background = on ? Theme.Lamp : new SolidColorBrush(Color.FromArgb(0x80, 0x83, 0x8C, 0x99));
            }
            current = index;
        }
        for (var i = 0; i < slides.Count; i++)
        {
            var index = i;
            var bar = new Border { Width = 26, Height = 3, CornerRadius = new CornerRadius(1.5) };
            var hit = Ui.Bare(bar, () =>
            {
                Show(index);
                timer.Stop();          // start the five seconds again from here
                timer.Start();
            }, slides[i].Str("title"));
            hit.Padding = new Thickness(0, 8, 0, 8);
            hit.Margin = new Thickness(6, 0, 0, 0);
            bars.Children.Add(hit);
        }
        timer.Tick += (_, _) =>
        {
            // Hold still while the player is up or the pointer is over the words.
            if (win.Player.IsOpen || texts[current].IsMouseOver) return;
            Show((current + 1) % slides.Count);
        };
        Show(0);
        grid.Loaded += (_, _) => { if (slides.Count > 1) timer.Start(); };
        grid.Unloaded += (_, _) => timer.Stop();
        return grid;
    }

    static FrameworkElement BillboardText(MainWindow win, Row hero)
    {
        var facts = new List<string>();
        if (hero.Truthy("year")) facts.Add(hero.Str("year"));
        if (hero.Truthy("certificate")) facts.Add(hero.Str("certificate"));
        if (hero.Double("rating") is > 0) facts.Add("★ " + Ui.Rating(hero.Double("rating")));
        facts.AddRange(Ui.Strings(hero.Get("genres")));

        var kind = Ui.Caps(hero.Str("kind") == "show" ? "Series" : "Film", 10.2, Theme.Lamp, 0.18, mono: true);
        var kindBox = new Border
        {
            BorderBrush = Theme.Lamp, BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 1, 0, 1), Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left, Child = kind,
        };
        var title = Ui.Text(hero.Str("title").ToUpperInvariant(), 48, Theme.Paper, FontWeights.Bold, wrap: true, margin: new Thickness(0, 0, 0, 8));
        title.LineHeight = 49;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        title.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 2, Direction = 270, Opacity = 0.6, Color = Colors.Black };

        var resume = hero.Get("resume") as Row;
        var play = Ui.Button(resume?.Str("reason") == "resume" ? "Resume" : "Play", () =>
        {
            if (resume != null) win.Play(resume.Long("file_id") ?? 0, resume.Double("position") ?? 0);
        }, primary: true);
        play.Padding = new Thickness(30, 11, 30, 11);
        play.FontSize = 14.25;
        play.IsEnabled = resume != null;
        var more = Ui.Button("More information", () => Open(win, hero));
        more.Padding = new Thickness(22, 11, 22, 11);
        more.FontSize = 14.25;
        more.Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x2A, 0x2F, 0x38));
        more.BorderBrush = Theme.Clear;
        more.Foreground = Theme.Paper;

        var text = new StackPanel
        {
            MaxWidth = 640,
            Margin = new Thickness(24, 0, 24, 34),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        text.Children.Add(kindBox);
        text.Children.Add(title);
        if (hero.Truthy("tagline"))
        {
            var tag = Ui.Text(hero.Str("tagline"), 15, Theme.Paper, wrap: true, margin: new Thickness(0, 0, 0, 10));
            tag.FontStyle = FontStyles.Italic;
            tag.Opacity = 0.86;
            text.Children.Add(tag);
        }
        text.Children.Add(Ui.Mono(string.Join("  ·  ", facts), Theme.Dim).Margin(0, 0, 0, 12));
        if (hero.Truthy("overview"))
        {
            var overview = Ui.Text(hero.Str("overview"), 15, Ui.Body, wrap: true, margin: new Thickness(0, 0, 0, 20));
            overview.LineHeight = 22.5;
            overview.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            overview.MaxHeight = 22.5 * 3;
            overview.MaxWidth = 470;
            overview.HorizontalAlignment = HorizontalAlignment.Left;
            overview.TextTrimming = TextTrimming.WordEllipsis;
            text.Children.Add(overview);
        }
        text.Children.Add(Ui.Actions(play, more));
        return text;
    }

    // --------------------------------------------------------------- library
    sealed class TabState
    {
        public string Sort = "recent";
        public string Genre = "";
        public bool Grid;
    }

    static readonly Dictionary<string, TabState> Tabs = new() { ["movie"] = new(), ["show"] = new() };

    public static void ShowGenre(MainWindow win, string kind, string genre)
    {
        var state = Tabs[kind == "show" ? "show" : "movie"];
        state.Genre = genre;
        state.Grid = true;
        win.Navigate(new Location(kind == "show" ? "shows" : "movies"));
    }

    static readonly (string Key, string Label)[] SortOptions =
    {
        ("recent", "Recently added"), ("popularity", "Popularity"), ("watched", "Most watched"), ("rating", "Rating"),
        ("title", "Title A–Z"), ("year", "Newest first"), ("genre", "Genre"), ("runtime", "Longest"),
    };

    static async Task<FrameworkElement> Library(MainWindow win, string kind)
    {
        var state = Tabs[kind];
        var browsing = state.Grid || state.Genre.Length > 0 || state.Sort != "recent";
        var genres = await Task.Run(() => Catalog.Genres(kind));
        var stats = await Task.Run(Catalog.Stats);

        var page = new StackPanel();
        var sort = Ui.Select(SortOptions, state.Sort, key =>
        {
            state.Sort = key;
            state.Grid = true;
            win.Refresh();
        });
        sort.Margin = new Thickness(0, 0, 8, 0);
        var view = Ui.Segmented(browsing ? 1 : 0,
            ("Rows", () =>
            {
                // Rows ignore sorting and filtering, so going back to them clears both.
                state.Grid = false;
                state.Genre = "";
                state.Sort = "recent";
                win.Refresh();
            }),
            ("All titles", () => { state.Grid = true; win.Refresh(); }));
        page.Children.Add(Ui.PageHead(kind == "show" ? "TV Shows" : "Movies",
            $"{(kind == "show" ? stats.Long("shows") : stats.Long("movies"))} titles", Ui.Row(sort, view)));

        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        chips.Children.Add(Ui.Chip("All genres", state.Genre.Length == 0, () => { state.Genre = ""; win.Refresh(); }));
        foreach (var g in genres)
        {
            var name = g.Str("name");
            chips.Children.Add(Ui.Chip(name, state.Genre == name, () =>
            {
                state.Genre = name;
                state.Grid = true;
                win.Refresh();
            }, g.Long("count")));
        }
        page.Children.Add(chips);

        if (browsing)
        {
            var data = await Task.Run(() => Catalog.LibraryPage(kind, state.Genre, null, state.Sort, limit: 500));
            page.Children.Add(data.Items.Count > 0
                ? PosterGrid(win, data.Items)
                : Ui.Empty("Nothing matched", ""));
        }
        else
        {
            var rows = await Task.Run(() => Catalog.TabHome(kind));
            if (rows.Count == 0)
                page.Children.Add(Ui.Empty("The shelf is empty", "",
                    Ui.Button("Add a folder", () => win.Navigate(new Location("settings")), primary: true)));
            foreach (var row in rows)
            {
                var genre = row.Genre;
                page.Children.Add(Shelf(win, row.Title, row.Items, row.Kind == "continue",
                    genre.Length > 0 ? () => { state.Genre = genre; state.Grid = true; win.Refresh(); } : null));
            }
        }
        return Ui.Page(page);
    }

    static async Task<FrameworkElement> Search(MainWindow win, string q)
    {
        var data = await Task.Run(() => Catalog.LibraryPage(null, null, q, "title", limit: 500));
        var page = Ui.Column(Ui.PageHead($"Results for “{q}”", $"{data.Total} title{(data.Total == 1 ? "" : "s")}"));
        page.Children.Add(data.Items.Count > 0
            ? PosterGrid(win, data.Items)
            : Ui.Empty("Nothing matched", ""));
        return Ui.Page(page);
    }

    // ---------------------------------------------------------------- detail
    static async Task<FrameworkElement> Detail(MainWindow win, Location where)
    {
        var data = await Task.Run(() => Catalog.MediaDetail(where.Id));
        if (data == null) return Ui.Page(Ui.Empty("Not found", ""));
        win.NamePage(where, data.Str("title"));
        var isShow = data.Str("kind") == "show";
        var seasons = Ui.Rows(data.Get("seasons"));

        // hero: the backdrop faint behind everything, then the spine, the case
        // and the words. The backdrop is a background, so it never sets the
        // hero's height — the hero is exactly as tall as what is in it.
        var hero = new Grid();
        var backdrop = new Border();
        Ui.Cover(backdrop, data.Str("backdrop"), opacity: 0.28);
        hero.Children.Add(backdrop);
        hero.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0x9E, 0x10, 0x13, 0x18), 0),
                    new(Color.FromArgb(0xDB, 0x10, 0x13, 0x18), 0.55),
                    new(Theme.VaultC, 1),
                },
            },
        });

        var inner = new Grid { Margin = new Thickness(24, 44, 24, 30), MaxWidth = 1680 };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // the spine: a case standing on a shelf, read from the edge
        var bits = new List<string> { data.Truthy("certificate") ? data.Str("certificate") : "NR", data.Truthy("year") ? data.Str("year") : "————" };
        if (data.Double("rating") is > 0) bits.Add("★ " + Ui.Rating(data.Double("rating")));
        var spineText = new Tracked { Family = Theme.Mono, Size = 10.8, Em = 0.22 };
        spineText.Add(bits[0].ToUpperInvariant(), Theme.Lamp, FontWeights.SemiBold);
        spineText.Add((" · " + string.Join(" · ", bits.Skip(1))).ToUpperInvariant(), Theme.Dim);
        spineText.LayoutTransform = new RotateTransform(-90);
        spineText.HorizontalAlignment = HorizontalAlignment.Center;
        spineText.VerticalAlignment = VerticalAlignment.Center;
        var spine = new Border
        {
            Width = 34,
            Background = Theme.Shelf,
            BorderBrush = Theme.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(0, 14, 0, 14),
            Margin = new Thickness(0, 0, 30, 0),
            Child = spineText,
        };
        var spineHost = new Grid();
        spineHost.Children.Add(spine);
        spineHost.Children.Add(new Border { Width = 3, Background = Theme.Lamp, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2, 0, 0, 2) });
        inner.Children.Add(spineHost);

        var posterUrl = data.Truthy("poster") ? data.Str("poster") : data.Str("thumb");
        var poster = new Border
        {
            Width = 220,
            Height = 330,
            CornerRadius = new CornerRadius(3),
            Background = Theme.Rail,
            BorderBrush = Theme.Edge,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 30, 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, Opacity = 0.45, ShadowDepth = 12, Direction = 270, Color = Colors.Black },
        };
        var posterArt = new Border { CornerRadius = new CornerRadius(2) };
        if (posterUrl.Length > 0) Ui.Cover(posterArt, posterUrl, decode: 440);
        else posterArt.Child = Ui.Mono("no poster", Theme.Dimmer, 10.5).Margin(0, 150, 0, 0);
        if (posterArt.Child is FrameworkElement np) np.HorizontalAlignment = HorizontalAlignment.Center;
        poster.Child = posterArt;
        Grid.SetColumn(poster, 1);
        inner.Children.Add(poster);

        var main = new StackPanel();
        Grid.SetColumn(main, 2);
        main.Children.Add(Ui.Caps((isShow ? "Series" : "Film") +
            (data.Truthy("scrape_source") ? $" · via {data.Str("scrape_source")}" : " · not scraped yet"), 10.8, Theme.Lamp, 0.18, mono: true)
            .Margin(0, 0, 0, 8));
        var h1 = Ui.Text(data.Str("title").ToUpperInvariant(), 40, Theme.Paper, FontWeights.ExtraBold, wrap: true, margin: new Thickness(0, 0, 0, 6));
        h1.LineHeight = 42;
        h1.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        main.Children.Add(h1);
        if (data.Truthy("tagline"))
        {
            var tag = Ui.Text(data.Str("tagline"), 15, Theme.Dim, wrap: true, margin: new Thickness(0, 0, 0, 12));
            tag.FontStyle = FontStyles.Italic;
            main.Children.Add(tag);
        }

        var facts = new WrapPanel { Margin = new Thickness(0, 12, 0, 10) };
        if (data.Truthy("certificate")) facts.Children.Add(Ui.Badge(data.Str("certificate"), Theme.Cert));
        if (data.Double("rating") is > 0)
            facts.Children.Add(Ui.Badge(Ui.Rating(data.Double("rating")) + "/10" +
                (data.Long("votes") is > 0 ? $" · {data.Long("votes"):N0} votes" : ""), Theme.Lamp));
        if (data.Truthy("year")) facts.Children.Add(Ui.Badge(data.Str("year")));
        if (data.Long("runtime") is > 0) facts.Children.Add(Ui.Badge(Ui.Minutes(data.Long("runtime")) + (isShow ? " / ep" : "")));
        if (isShow && data.Long("episode_count") is > 0)
            facts.Children.Add(Ui.Badge($"{seasons.Count} season{(seasons.Count == 1 ? "" : "s")} · {data.Long("episode_count")} episodes"));
        if (data.Truthy("status")) facts.Children.Add(Ui.Badge(data.Str("status")));
        main.Children.Add(facts);

        var genres = Ui.Strings(data.Get("genres"));
        if (genres.Count > 0)
        {
            var links = Ui.GenreLinks(genres, g => ShowGenre(win, data.Str("kind"), g));
            links.Margin = new Thickness(0, 0, 0, 12);
            main.Children.Add(links);
        }

        // Synopses can run to several hundred words; scrolling keeps the hero
        // a steady height instead of the artwork growing to match the text.
        var overview = data.Truthy("overview")
            ? Ui.Text(data.Str("overview"), 15, Ui.Body, wrap: true)
            : Ui.Text("", 15, Theme.Dim, wrap: true);
        overview.LineHeight = 22.5;
        overview.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var overviewScroll = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(0, 0, 12, 0), Child = overview },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 22.5 * 7.7,
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
            Focusable = false,
            Margin = new Thickness(0, 0, 0, 18),
        };
        Ui.ChainWheel(overviewScroll);
        main.Children.Add(overviewScroll);

        var resume = data.Get("resume") as Row;
        var epLabel = resume != null && resume.Get("season") != null
            ? $"S{resume.Long("season") ?? 0:00}E{resume.Long("episode") ?? 0:00}" : "";
        var playLabel = resume == null ? "No playable file"
            : resume.Str("reason") switch
            {
                "resume" => epLabel.Length > 0 ? $"Resume {epLabel}" : "Resume",
                "next" => $"Next up · {epLabel}",
                "rewatch" => "Watch again",
                _ => isShow ? $"Play {(epLabel.Length > 0 ? epLabel : "first episode")}" : "Play",
            };
        var play = Ui.Button(playLabel, () =>
        {
            if (resume != null) win.Play(resume.Long("file_id") ?? 0, resume.Double("position") ?? 0);
        }, primary: true);
        play.IsEnabled = resume != null;
        var actions = Ui.Actions(play, Ui.Button("Edit details", () => EditDialog.Open(win, where.Id)));
        if (data.Truthy("trailer"))
        {
            var trailer = Ui.Button("Trailer", () => OpenUrl(data.Str("trailer")));
            trailer.Margin = new Thickness(0, 0, 10, 10);
            actions.Children.Add(trailer);
        }
        foreach (UIElement a in actions.Children)
            if (a is Button b) b.VerticalAlignment = VerticalAlignment.Center;
        main.Children.Add(actions);
        inner.Children.Add(main);
        hero.Children.Add(inner);

        var body = new StackPanel();
        if (isShow && seasons.Count > 0) body.Children.Add(SeasonSection(win, data, seasons, resume));
        if (!isShow) body.Children.Add(FilesSection(win, data));

        var cast = Ui.Rows(data.Get("cast"));
        if (cast.Count > 0)
            body.Children.Add(Ui.Section("Cast", PeopleGrid(win, cast.Take(18).Select(p => (p, p.Str("character"))))));
        var crew = Ui.Rows(data.Get("crew"));
        if (crew.Count > 0)
        {
            var lines = new StackPanel();
            foreach (var group in crew.GroupBy(c => c.Str("job")))
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), FontSize = 14 };
                line.Inlines.Add(new System.Windows.Documents.Run(group.Key.ToUpperInvariant() + "   ")
                {
                    FontFamily = Theme.Mono, FontSize = 10.8, Foreground = Theme.Dim,
                });
                line.Inlines.Add(new System.Windows.Documents.Run(string.Join(", ", group.Select(c => c.Str("name")))) { Foreground = Theme.Paper });
                lines.Children.Add(line);
            }
            body.Children.Add(Ui.Section("Crew", lines));
        }
        var similar = Ui.Rows(data.Get("similar"));
        if (similar.Count > 0)
        {
            var cards = similar.Select(i => (UIElement)Cards.Poster(i, r => Open(win, r)));
            body.Children.Add(Ui.Section("More like this", Ui.Strip(cards, Cards.PosterStep)));
        }
        return Ui.Bleed(hero, body);
    }

    // The season picked is remembered per show for as long as the app is open.
    static readonly Dictionary<long, long> SeasonMemory = new();

    static FrameworkElement SeasonSection(MainWindow win, Row show, List<Row> seasons, Row? resume)
    {
        var showId = show.Long("id") ?? 0;
        var picker = new ComboBox
        {
            MinWidth = 190,
            Height = 42,
            FontSize = 14.25,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(17, 0, 34, 0),
        };
        foreach (var s in seasons)
        {
            var count = Ui.Rows(s.Get("episodes")).Count;
            picker.Items.Add(new ComboBoxItem
            {
                Content = $"{s.Str("name")} · {count} episode{(count == 1 ? "" : "s")}",
                Tag = s.Long("number") ?? 0,
                FontWeight = FontWeights.Normal,
            });
        }
        // the amber edge down the picker's left side
        var pickerHost = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        pickerHost.Children.Add(picker);
        pickerHost.Children.Add(new Border
        {
            Width = 3, Background = Theme.Lamp, HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(3, 0, 0, 3), IsHitTestVisible = false,
        });
        var summary = Ui.Mono("", Theme.Dim);
        summary.VerticalAlignment = VerticalAlignment.Center;
        summary.Margin = new Thickness(12, 0, 0, 0);
        var bar = Ui.Row(pickerHost, summary);
        bar.Margin = new Thickness(0, 0, 0, 16);
        var list = new StackPanel();

        void Draw(long number)
        {
            SeasonMemory[showId] = number;
            var season = seasons.FirstOrDefault(s => s.Long("number") == number) ?? seasons[0];
            var eps = Ui.Rows(season.Get("episodes"));
            var watched = eps.Count(e => e.Truthy("completed"));
            summary.Text = $"{watched}/{eps.Count} watched" + (season.Truthy("air_date") ? $" · first aired {season.Str("air_date")}" : "");
            list.Children.Clear();
            foreach (var ep in eps)
                list.Children.Add(Cards.Episode(ep, e => win.Play(e.Long("file_id") ?? 0,
                    e.Truthy("completed") ? 0 : e.Double("position") ?? 0)));
        }

        long start = SeasonMemory.TryGetValue(showId, out var remembered) ? remembered
            : resume?.Long("season") ?? seasons[0].Long("number") ?? 0;
        var index = seasons.FindIndex(s => s.Long("number") == start);
        picker.SelectedIndex = index >= 0 ? index : 0;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: long n }) Draw(n);
        };
        Draw(seasons[picker.SelectedIndex].Long("number") ?? 0);
        return Ui.Section("Episodes", bar, list);
    }

    static FrameworkElement FilesSection(MainWindow win, Row movie)
    {
        var files = Ui.Rows(movie.Get("files"));
        if (files.Count == 0) return new StackPanel();
        var rows = new StackPanel();
        foreach (var f in files)
        {
            var info = new List<string> { f.Str("quality"), Ui.Runtime(f.Double("duration")), Ui.Bytes(f.Double("size") ?? 0),
                                          f.Str("vcodec"), f.Str("acodec") };
            if (f.Truthy("missing")) info.Add("MISSING");
            var text = Ui.Column(
                Ui.Text(f.Str("filename"), 15, Theme.Paper, FontWeights.Bold, margin: new Thickness(0, 0, 0, 4)),
                Ui.Mono(f.Str("path"), Theme.Dim),
                Ui.Mono(string.Join("  ·  ", info.Where(x => x.Length > 0)), Theme.Dimmer).Margin(0, 5, 0, 0));
            var fileId = f.Long("id") ?? 0;
            var path = f.Str("path");
            var folder = Ui.Button("Show in folder", () => ShowInFolder(path));
            var rebuild = Ui.Button("Rebuild poster frame", async () =>
            {
                var (ok, message, _) = await Task.Run(() => Editing.RebuildArtwork(fileId));
                Images.Clear();
                if (!ok) win.Toast(message, true);
                if (ok) win.Refresh();
            });
            var side = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            foreach (var b in new[] { folder, rebuild })
            {
                b.HorizontalAlignment = HorizontalAlignment.Right;
                b.Margin = new Thickness(0, 0, 0, 6);
                side.Children.Add(b);
            }
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            side.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(side, 1);
            grid.Children.Add(text);
            grid.Children.Add(side);
            rows.Children.Add(new Border
            {
                Background = Theme.Shelf, BorderBrush = Theme.Rail, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 12, 12, 6), Margin = new Thickness(0, 0, 0, 10), Child = grid,
            });
        }
        return Ui.Section("Files", rows);
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MainWindow.Current?.Toast("Could not open that: " + ex.Message, true); }
    }

    public static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
            else MainWindow.Current?.Toast("File not found.", true);
        }
        catch (Exception ex) { MainWindow.Current?.Toast(ex.Message, true); }
    }

    // ---------------------------------------------------------------- people
    static string _peopleSort = "credits";

    static async Task<FrameworkElement> Actors(MainWindow win)
    {
        var (items, total) = await Task.Run(() => Catalog.People(null, _peopleSort, 400));
        var page = new StackPanel();
        var sort = Ui.Select(new[] { ("credits", "Most titles"), ("name", "Name A–Z"), ("recent", "Recently added") }, _peopleSort,
            key => { _peopleSort = key; win.Refresh(); });
        page.Children.Add(Ui.PageHead("Actors", $"{total} people", sort));
        if (items.Count == 0)
        {
            page.Children.Add(Ui.Empty("No cast yet", ""));
            return Ui.Page(page);
        }
        page.Children.Add(PeopleGrid(win, items.Select(p =>
        {
            var credits = p.Long("credits") ?? 0;
            return (p, $"{credits} title{(credits == 1 ? "" : "s")}" + (p.Long("shows") is > 0 ? $" · {p.Long("shows")} TV" : ""));
        })));
        return Ui.Page(page);
    }

    static async Task<FrameworkElement> Person(MainWindow win, Location where)
    {
        var p = await Task.Run(() => Catalog.PersonDetail(where.Id));
        if (p == null) return Ui.Page(Ui.Empty("Not found", ""));
        win.NamePage(where, p.Str("name"));
        var credits = Ui.Rows(p.Get("credits"));
        var cast = credits.Where(c => c.Str("role") == "cast").ToList();
        var crew = credits.Where(c => c.Str("role") == "crew").ToList();

        var top = new Grid { Margin = new Thickness(0, 0, 0, 26) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var photo = new Border
        {
            Width = 200, Height = 266, CornerRadius = new CornerRadius(3), Background = Theme.Rail,
            BorderBrush = Theme.Edge, BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, Opacity = 0.45, ShadowDepth = 12, Direction = 270, Color = Colors.Black },
        };
        var photoArt = new Border { CornerRadius = new CornerRadius(2) };
        if (p.Truthy("photo")) Ui.Cover(photoArt, p.Str("photo"), decode: 400);
        else
        {
            var initials = Ui.Text(Ui.Initials(p.Str("name")), 21, Theme.Dimmer, mono: true);
            initials.HorizontalAlignment = HorizontalAlignment.Center;
            initials.VerticalAlignment = VerticalAlignment.Center;
            photoArt.Child = initials;
        }
        photo.Child = photoArt;
        top.Children.Add(photo);
        var main = new StackPanel { Margin = new Thickness(30, 0, 0, 0) };
        Grid.SetColumn(main, 1);
        main.Children.Add(Ui.Caps($"Actor · {credits.Count} titles", 10.8, Theme.Lamp, 0.18, mono: true).Margin(0, 0, 0, 8));
        var h1 = Ui.Text(p.Str("name").ToUpperInvariant(), 40, Theme.Paper, FontWeights.ExtraBold, wrap: true, margin: new Thickness(0, 0, 0, 6));
        h1.LineHeight = 42;
        h1.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        main.Children.Add(h1);
        var life = new[] { p.Truthy("birthday") ? $"Born {p.Str("birthday")}" : "", p.Truthy("deathday") ? $"Died {p.Str("deathday")}" : "", p.Str("birthplace") }
            .Where(x => x.Length > 0).ToList();
        if (life.Count > 0) main.Children.Add(Ui.Mono(string.Join("  ·  ", life), Theme.Dim).Margin(0, 0, 0, 14));
        var topGenres = Ui.Strings(p.Get("top_genres"));
        if (topGenres.Count > 0) main.Children.Add(Ui.GenreLinks(topGenres, g => ShowGenre(win, "movie", g)));
        var bio = Ui.Text(p.Str("bio"), 15, p.Truthy("bio") ? Ui.Body : Theme.Dim, wrap: true);
        bio.LineHeight = 22.5;
        bio.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        bio.MaxWidth = 700;
        bio.HorizontalAlignment = HorizontalAlignment.Left;
        bio.MaxHeight = 22.5 * 6;
        bio.TextTrimming = TextTrimming.WordEllipsis;
        main.Children.Add(bio);
        var actions = Ui.Actions();
        actions.Margin = new Thickness(0, 14, 0, 0);
        if (p.Str("bio").Length > 420)
        {
            Button? more = null;
            more = Ui.Link("Read more", () =>
            {
                var open = double.IsInfinity(bio.MaxHeight);
                bio.MaxHeight = open ? 22.5 * 6 : double.PositiveInfinity;
                ((TextBlock)more!.Content).Text = open ? "Read more" : "Show less";
            });
            more.Margin = new Thickness(0, 0, 14, 10);
            actions.Children.Add(more);
        }
        var personId = where.Id;
        foreach (var b in new[]
                 {
                     Ui.Button("Fetch biography", async () =>
                     {
                         var result = await Task.Run(() => Metadata.RefreshPerson(personId));
                         if (!result.Ok) win.Toast(result.Log.LastOrDefault() ?? "Nothing found", true);
                         Images.Clear();
                         win.Refresh();
                     }),
                     Ui.Button("Edit profile", () => EditDialog.OpenPerson(win, p)),
                     Ui.Button("Upload photo", () =>
                     {
                         var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures|*.jpg;*.jpeg;*.png;*.webp" };
                         if (dialog.ShowDialog(win) != true) return;
                         if (Editing.UploadImage("person", personId, dialog.FileName) == null) win.Toast("Use a JPG, PNG or WebP picture.", true);
                         Images.Clear();
                         win.Refresh();
                     }),
                 })
        {
            b.Margin = new Thickness(0, 0, 10, 10);
            actions.Children.Add(b);
        }
        main.Children.Add(actions);
        top.Children.Add(main);

        var page = Ui.Column(top);
        if (cast.Count > 0) page.Children.Add(Ui.Section("Appears in", PosterGrid(win, cast)));
        if (crew.Count > 0) page.Children.Add(Ui.Section("Also credited", PosterGrid(win, crew)));
        return Ui.Page(page);
    }
}
