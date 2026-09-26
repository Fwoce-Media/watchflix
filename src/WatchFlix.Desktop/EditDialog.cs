using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WatchFlix.Core.Data;
using WatchFlix.Core.Library;
using WatchFlix.Core.Providers;

namespace WatchFlix.Desktop;

/// <summary>
/// Edit details: correct a title by hand, re-scrape it, pick the right match
/// when the automatic one was wrong, choose artwork, fix cast and genres.
/// </summary>
public static class EditDialog
{
    public static void Open(MainWindow win, long mediaId)
    {
        var data = Catalog.MediaDetail(mediaId);
        if (data == null) return;
        var w = Dialogs.Create(win, $"Edit details · {data.Str("title")}", 860, 720);
        var changed = false;
        w.Closed += (_, _) => { if (changed) { Images.Clear(); win.Refresh(); } };

        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        var tabBar = new Border { BorderBrush = Theme.Rail, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 20), Child = tabs };
        var host = new ContentControl();
        var grid = new Grid { Margin = new Thickness(24, 18, 24, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(tabBar);
        var scroll = new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        w.Content = grid;

        var pages = new (string Label, Func<FrameworkElement> Build)[]
        {
            ("Details", () => DetailsTab(win, w, mediaId, data, () => changed = true)),
            ("Scrape", () => ScrapeTab(win, w, mediaId, data, () => changed = true)),
            ("Artwork", () => ArtworkTab(win, w, mediaId, data, () => changed = true)),
            ("Cast & genres", () => CastTab(win, w, mediaId, data, () => changed = true)),
        };
        var labels = new List<(TextBlock Text, Border Line)>();
        void Show(int index)
        {
            for (var i = 0; i < labels.Count; i++)
            {
                labels[i].Text.Foreground = i == index ? Theme.Paper : Theme.Dim;
                labels[i].Line.BorderBrush = i == index ? Theme.Lamp : Theme.Clear;
            }
            host.Content = pages[index].Build();
        }
        for (var i = 0; i < pages.Length; i++)
        {
            var index = i;
            var text = Ui.Text(pages[i].Label, 12.75, Theme.Dim, FontWeights.Bold);
            var line = new Border { BorderThickness = new Thickness(0, 0, 0, 2), BorderBrush = Theme.Clear, Padding = new Thickness(14, 8, 14, 8), Child = text };
            var b = Ui.Bare(line, () => Show(index));
            b.Margin = new Thickness(0, 0, 4, -1);
            b.MouseEnter += (_, _) => { if (line.BorderBrush == Theme.Clear) text.Foreground = Theme.Paper; };
            b.MouseLeave += (_, _) => { if (line.BorderBrush == Theme.Clear) text.Foreground = Theme.Dim; };
            labels.Add((text, line));
            tabs.Children.Add(b);
        }
        Show(0);
        w.Show();
    }

    static TextBox Box(string text, bool multi = false)
    {
        var box = new TextBox { Text = text };
        if (multi)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 110;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        return box;
    }

    static FrameworkElement Labelled(string label, UIElement input)
    {
        return Ui.Field(label, input);
    }

    static FrameworkElement Pair(FrameworkElement a, FrameworkElement b)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        a.Margin = new Thickness(0, 0, 8, a.Margin.Bottom);
        b.Margin = new Thickness(8, 0, 0, b.Margin.Bottom);
        Grid.SetColumn(b, 1);
        g.Children.Add(a);
        g.Children.Add(b);
        return g;
    }

    static object? NumberOrNull(string text, bool integer)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (integer) return long.TryParse(text, out var l) ? l : null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    // ----------------------------------------------------------------- details
    static FrameworkElement DetailsTab(MainWindow win, Window w, long id, Row data, Action changed)
    {
        var title = Box(data.Str("title"));
        var year = Box(data.Str("year"));
        var cert = Box(data.Str("certificate"));
        var rating = Box(data.Double("rating") is double r ? r.ToString("0.0", CultureInfo.InvariantCulture) : "");
        var released = Box(data.Str("release_date"));
        var runtime = Box(data.Str("runtime"));
        var status = Box(data.Str("status"));
        var tagline = Box(data.Str("tagline"));
        var overview = Box(data.Str("overview"), multi: true);
        var trailer = Box(data.Str("trailer"));
        var imdb = Box(data.Str("imdb_id"));
        var kind = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        kind.Items.Add(new ComboBoxItem { Content = "Film", Tag = "movie" });
        kind.Items.Add(new ComboBoxItem { Content = "Series", Tag = "show" });
        kind.SelectedIndex = data.Str("kind") == "show" ? 1 : 0;
        var locked = new CheckBox
        {
            Content = Ui.Text("Lock", 13.5, Theme.Paper),
            IsChecked = data.Truthy("locked"),
            Margin = new Thickness(0, 4, 0, 16),
        };

        var save = Ui.Button("Save", () =>
        {
            var fields = new Dictionary<string, object?>
            {
                ["title"] = title.Text.Trim().Length > 0 ? title.Text.Trim() : data.Str("title"),
                ["year"] = NumberOrNull(year.Text, true),
                ["certificate"] = cert.Text.Trim(),
                ["rating"] = NumberOrNull(rating.Text, false),
                ["release_date"] = released.Text.Trim(),
                ["runtime"] = NumberOrNull(runtime.Text, true),
                ["status"] = status.Text.Trim(),
                ["tagline"] = tagline.Text.Trim(),
                ["overview"] = overview.Text.Trim(),
                ["trailer"] = trailer.Text.Trim(),
                ["imdb_id"] = imdb.Text.Trim(),
                ["kind"] = (kind.SelectedItem as ComboBoxItem)?.Tag as string ?? data.Str("kind"),
                ["locked"] = locked.IsChecked == true ? 1 : 0,
            };
            Editing.UpdateMedia(id, fields);
            changed();
            w.Close();
        }, primary: true);
        save.HorizontalAlignment = HorizontalAlignment.Left;

        var remove = Ui.Button("Remove from library…", () =>
        {
            if (!Dialogs.Confirm(w, "Remove from library",
                    Ui.Text(data.Str("title"), 15, Theme.Paper, FontWeights.SemiBold, wrap: true), "Remove", height: 200)) return;
            Editing.RemoveMedia(id);
            w.Close();
            win.GoBack();
            win.Refresh();
        });
        remove.Margin = new Thickness(10, 0, 0, 0);

        return Ui.Column(
            Labelled("Title", title),
            Pair(Labelled("Year", year), Labelled("Kind", kind)),
            Pair(Labelled("Certificate", cert), Labelled("Rating out of 10", rating)),
            Pair(Labelled("Release date", released), Labelled("Runtime in minutes", runtime)),
            Pair(Labelled("Status", status), Labelled("IMDb id", imdb)),
            Labelled("Tagline", tagline),
            Labelled("Description", overview),
            Labelled("Trailer link", trailer),
            locked,
            Ui.Row(save, remove));
    }

    // ------------------------------------------------------------------ scrape
    static FrameworkElement ScrapeTab(MainWindow win, Window w, long id, Row data, Action changed)
    {
        var title = Box(data.Str("title"));
        var year = Box(data.Str("year"));
        var log = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var results = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var providers = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var p in WatchFlix.Core.Library.Metadata.ProviderStatus())
            providers.Children.Add(Ui.Mono($"{p.Name}: {p.State}" + (p.LastError.Length > 0 ? $" — {p.LastError}" : ""),
                p.State == "ready" ? Theme.Good : p.State == "needs key" ? Theme.Lamp : Theme.Cert, 12));

        void Log(IEnumerable<string> lines, bool ok)
        {
            log.Children.Clear();
            foreach (var line in lines) log.Children.Add(Ui.Mono(line, ok ? Theme.Dim : Theme.Cert, 12).Margin(0, 0, 0, 3));
        }

        Button? scrapeButton = null;
        scrapeButton = Ui.Button("Scrape now", async () =>
        {
            scrapeButton!.IsEnabled = false;
            Log(new[] { "Asking the providers…" }, true);
            var t = title.Text.Trim();
            var y = NumberOrNull(year.Text, true) is long l ? (int?)l : null;
            var result = await Task.Run(() => WatchFlix.Core.Library.Metadata.ScrapeMedia(id, t, y, yearGiven: true));
            Log(result.Log, result.Ok);
            scrapeButton.IsEnabled = true;
            if (result.Ok)
            {
                changed();
                win.Toast($"Matched “{result.Title}” via {result.Source}.");
            }
        }, primary: true);

        Button? matchesButton = null;
        matchesButton = Ui.Button("Show all matches", async () =>
        {
            matchesButton!.IsEnabled = false;
            results.Children.Clear();
            results.Children.Add(Ui.Mono("Searching…", Theme.Dim));
            var t = title.Text.Trim();
            var y = NumberOrNull(year.Text, true) is long l ? (int?)l : null;
            var kind = data.Str("kind");
            var (items, errors) = await Task.Run(() => WatchFlix.Core.Library.Metadata.Candidates(t, y, kind));
            matchesButton.IsEnabled = true;
            results.Children.Clear();
            if (errors.Count > 0) Log(errors, false);
            if (items.Count == 0)
            {
                results.Children.Add(Ui.Mono("No matches", Theme.Dim));
                return;
            }
            foreach (var hit in items) results.Children.Add(Candidate(win, w, id, kind, hit, Log, changed));
        });
        matchesButton.Margin = new Thickness(10, 0, 0, 0);

        return Ui.Column(
            providers,
            Pair(Labelled("Title to search for", title), Labelled("Year", year)),
            Ui.Row(scrapeButton, matchesButton),
            log,
            results);
    }

    static FrameworkElement Candidate(MainWindow win, Window w, long id, string kind, Candidate hit,
                                      Action<IEnumerable<string>, bool> log, Action changed)
    {
        var art = new Border
        {
            Width = 120, Height = 180, Background = Theme.Rail, CornerRadius = new CornerRadius(3), ClipToBounds = true,
            Child = hit.Poster.Length > 0 ? Images.Lazy(hit.Poster, 240) : null,
        };
        var body = Ui.Column(art,
            Ui.Text(hit.Title, 12.5, Theme.Paper, FontWeights.SemiBold, wrap: true, margin: new Thickness(0, 6, 0, 0)),
            Ui.Mono($"{hit.Year?.ToString() ?? "—"} · {hit.Provider} · {Math.Round(hit.Score * 100)}%", Theme.Dim, 11));
        body.Width = 120;
        var button = Ui.Bare(body, async () =>
        {
            log(new[] { $"Applying {hit.Title} from {hit.Provider}…" }, true);
            var result = await Task.Run(() => WatchFlix.Core.Library.Metadata.ApplyMatch(id, hit.Provider, hit.Id, kind));
            log(result.Ok ? new[] { $"Applied “{result.Title}” via {result.Source}." } : result.Log, result.Ok);
            if (result.Ok)
            {
                changed();
                win.Toast($"Now matched to “{result.Title}”.");
                w.Close();
            }
        }, "Use this match");
        button.Margin = new Thickness(0, 0, 14, 14);
        return button;
    }

    // ----------------------------------------------------------------- artwork
    static FrameworkElement ArtworkTab(MainWindow win, Window w, long id, Row data, Action changed)
    {
        var stack = new StackPanel();
        var options = data.Get("artwork") as List<Dictionary<string, string>> ?? new List<Dictionary<string, string>>();

        foreach (var (field, label, width, height) in new[] { ("poster", "Poster", 140.0, 210.0), ("backdrop", "Backdrop", 280.0, 158.0) })
        {
            stack.Children.Add(Ui.Text(label, 15, Theme.Paper, FontWeights.SemiBold, margin: new Thickness(0, 6, 0, 10)));
            var wrap = new WrapPanel();
            var currentUrl = data.Str(field);
            if (currentUrl.Length > 0)
                wrap.Children.Add(Picture(currentUrl, "current", width, height, true, null));
            foreach (var option in options)
            {
                if (!option.TryGetValue(field, out var url) || string.IsNullOrEmpty(url)) continue;
                var source = option.TryGetValue("source", out var s) ? s : "";
                var f = field;
                wrap.Children.Add(Picture(url, source, width, height, false, () =>
                {
                    var ok = Editing.ChooseArtwork(id, f == "poster" ? url : null, f == "backdrop" ? url : null, data.Str("title"));
                    if (ok) { changed(); w.Close(); }
                    else win.Toast("That picture could not be used.", true);
                }));
            }
            stack.Children.Add(wrap);
            var fieldName = field;
            var upload = Ui.Button($"Use my own {label.ToLowerInvariant()}…", () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures|*.jpg;*.jpeg;*.png;*.webp" };
                if (dialog.ShowDialog(w) != true) return;
                if (Editing.UploadImage(fieldName, id, dialog.FileName) == null)
                {
                    win.Toast("Use a JPG, PNG or WebP picture.", true);
                    return;
                }
                changed();
                w.Close();
            });
            upload.HorizontalAlignment = HorizontalAlignment.Left;
            upload.Margin = new Thickness(0, 0, 0, 18);
            stack.Children.Add(upload);
        }
        return stack;
    }

    static FrameworkElement Picture(string url, string caption, double width, double height, bool current, Action? pick)
    {
        var art = new Border
        {
            Width = width, Height = height, Background = Theme.Rail, CornerRadius = new CornerRadius(3), ClipToBounds = true,
            BorderBrush = current ? Theme.Lamp : Theme.Clear, BorderThickness = new Thickness(current ? 2 : 0),
            Child = Images.Lazy(url, (int)(width * 2)),
        };
        var body = Ui.Column(art, Ui.Mono(caption, current ? Theme.Lamp : Theme.Dim, 11).Margin(0, 5, 0, 0));
        FrameworkElement element = pick != null ? Ui.Bare(body, pick, "Use this one") : body;
        element.Margin = new Thickness(0, 0, 14, 14);
        return element;
    }

    // ------------------------------------------------------------ cast, genres
    static FrameworkElement CastTab(MainWindow win, Window w, long id, Row data, Action changed)
    {
        var genres = Box(string.Join(", ", Ui.Strings(data.Get("genres"))));
        var cast = Box(string.Join(Environment.NewLine, Ui.Rows(data.Get("cast")).Select(c =>
            c.Truthy("character") ? $"{c.Str("name")} as {c.Str("character")}" : c.Str("name"))), multi: true);
        cast.MinHeight = 300;
        var save = Ui.Button("Save", () =>
        {
            var genreList = genres.Text.Split(',').Select(g => g.Trim()).Where(g => g.Length > 0).ToList();
            var castList = cast.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l =>
            {
                var at = l.IndexOf(" as ", StringComparison.OrdinalIgnoreCase);
                return at > 0 ? (l[..at].Trim(), l[(at + 4)..].Trim()) : (l, "");
            }).ToList();
            Editing.UpdateMedia(id, new Dictionary<string, object?>(), genreList, castList);
            changed();
            w.Close();
        }, primary: true);
        save.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.Column(
            Labelled("Genres", genres),
            Labelled("Cast", cast),
            save);
    }

    // ------------------------------------------------------------------ people
    public static void OpenPerson(MainWindow win, Row person)
    {
        var id = person.Long("id") ?? 0;
        var w = Dialogs.Create(win, $"Edit profile · {person.Str("name")}", 640, 600);
        var name = Box(person.Str("name"));
        var born = Box(person.Str("birthday"));
        var died = Box(person.Str("deathday"));
        var place = Box(person.Str("birthplace"));
        var bio = Box(person.Str("bio"), multi: true);
        bio.MinHeight = 180;
        var locked = new CheckBox
        {
            Content = Ui.Text("Lock", 13.5, Theme.Paper),
            IsChecked = person.Truthy("locked"),
            Margin = new Thickness(0, 4, 0, 16),
        };
        var save = Ui.Button("Save", () =>
        {
            Editing.UpdatePerson(id, new Dictionary<string, object?>
            {
                ["name"] = name.Text.Trim().Length > 0 ? name.Text.Trim() : person.Str("name"),
                ["birthday"] = born.Text.Trim(),
                ["deathday"] = died.Text.Trim(),
                ["birthplace"] = place.Text.Trim(),
                ["bio"] = bio.Text.Trim(),
                ["locked"] = locked.IsChecked == true ? 1 : 0,
            });
            w.Close();
            win.Refresh();
        }, primary: true);
        save.HorizontalAlignment = HorizontalAlignment.Left;
        w.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = Ui.Column(
                Labelled("Name", name),
                Pair(Labelled("Born", born), Labelled("Died", died)),
                Labelled("Birthplace", place),
                Labelled("Biography", bio),
                locked,
                save).Margin(24, 20, 24, 20),
        };
        w.Show();
    }
}
