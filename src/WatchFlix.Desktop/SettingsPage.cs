using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WatchFlix.Core;
using WatchFlix.Core.Data;
using WatchFlix.Core.Library;
using WatchFlix.Core.Media;

namespace WatchFlix.Desktop;

/// <summary>
/// Laid out as the 3.0 Settings page was: sections under ruled headings, mono
/// field labels, dark boxes for folders and providers.
/// </summary>
public static class SettingsPage
{
    static CheckBox Tick(string label, string key, bool fallback, Action<bool>? after = null)
    {
        var box = new CheckBox
        {
            Content = Ui.Caps(label, 10.8, Theme.Dim, 0.08, mono: true),
            IsChecked = Settings.Bool(key, fallback),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        box.Checked += (_, _) => { Settings.Set(key, true); after?.Invoke(true); };
        box.Unchecked += (_, _) => { Settings.Set(key, false); after?.Invoke(false); };
        return box;
    }

    static TextBox Entry(string key)
    {
        var box = new TextBox { Text = Settings.Str(key), Height = 36, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 0, 10, 0) };
        box.LostFocus += (_, _) =>
        {
            if (box.Text.Trim() != Settings.Str(key)) Settings.Set(key, box.Text.Trim());
        };
        return box;
    }

    static StackPanel Para(params UIElement[] children)
    {
        var s = new StackPanel { MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static FrameworkElement Build(MainWindow win)
    {
        var page = new StackPanel { MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Ui.PageHead("Settings", $"{Config.AppName} {Config.AppVersion}"));
        page.Children.Add(Folders(win));
        page.Children.Add(Metadata(win));
        page.Children.Add(Previews(win));
        page.Children.Add(About(win));
        return Ui.Page(page);
    }

    // ---------------------------------------------------------------- folders
    static FrameworkElement RootList(MainWindow win, string key, string label)
    {
        var list = new StackPanel();
        void Draw()
        {
            list.Children.Clear();
            var roots = Settings.List(key);
            if (roots.Count == 0) list.Children.Add(Ui.Mono("None", Theme.Dimmer).Margin(0, 0, 0, 2));
            foreach (var root in roots)
            {
                var path = root;
                var exists = Directory.Exists(path);
                var row = new DockPanel { LastChildFill = true };
                var remove = Ui.Link("Remove", () =>
                {
                    Settings.SetList(key, Settings.List(key).Where(p => p != path));
                    Draw();
                });
                remove.Margin = new Thickness(14, 0, 0, 0);
                DockPanel.SetDock(remove, System.Windows.Controls.Dock.Right);
                row.Children.Add(remove);
                var open = Ui.Link("Open", () => Pages.ShowInFolder(path), Theme.Dim);
                open.Margin = new Thickness(14, 0, 0, 0);
                DockPanel.SetDock(open, System.Windows.Controls.Dock.Right);
                row.Children.Add(open);
                var text = Ui.Mono(path + (exists ? "" : "   — not found"), exists ? Theme.Paper : Theme.Cert);
                text.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(text);
                list.Children.Add(Ui.Box(row));
            }
        }
        Draw();

        var input = new TextBox { Height = 36, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 0, 10, 0) };
        void AddPath(string raw)
        {
            raw = raw.Trim().Trim('"');
            if (raw.Length == 0) return;
            if (!Directory.Exists(raw))
            {
                win.Toast("That folder does not exist.", true);
                return;
            }
            var chosen = PathUtil.Resolve(raw);
            var all = Settings.List(key);
            if (!all.Contains(chosen, StringComparer.OrdinalIgnoreCase)) all.Add(chosen);
            Settings.SetList(key, all);
            input.Text = "";
            Draw();
        }
        input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) AddPath(input.Text);
        };
        var browse = Ui.Button("Browse…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Add a folder: {label}" };
            if (dialog.ShowDialog(win) == true) AddPath(dialog.FolderName);
        });
        var add = Ui.Button("Add", () => AddPath(input.Text));
        browse.Margin = new Thickness(8, 0, 0, 0);
        add.Margin = new Thickness(8, 0, 0, 0);
        var entry = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(add, System.Windows.Controls.Dock.Right);
        DockPanel.SetDock(browse, System.Windows.Controls.Dock.Right);
        entry.Children.Add(add);
        entry.Children.Add(browse);
        entry.Children.Add(input);
        return Ui.Field(label, Ui.Column(list, entry));
    }

    static FrameworkElement Folders(MainWindow win)
    {
        var scan = Ui.Button("Scan now", () => win.StartScan(false), primary: true);
        var rescan = Ui.Button("Rescan and re-scrape everything", () =>
        {
            if (Dialogs.Confirm(win, "Re-scrape everything?", new Border(), "Re-scrape", height: 170))
                win.StartScan(true);
        });
        var missing = Ui.Button("Remove missing files", async () =>
        {
            var preview = await Task.Run(Editing.PreviewMissing);
            if (preview.Files.Count == 0)
            {
                win.Toast("Nothing missing.");
                return;
            }
            var list = new StackPanel();
            list.Children.Add(Ui.Text($"{preview.Files.Count} files · {preview.Titles.Count} titles", 14, Theme.Paper, FontWeights.SemiBold, margin: new Thickness(0, 0, 0, 12)));
            foreach (var f in preview.Files.Take(200))
                list.Children.Add(Ui.Mono($"{f.Str("title")}  —  {f.Str("path")}", Theme.Dim, 11.5).Margin(0, 0, 0, 4));
            if (!Dialogs.Confirm(win, "Remove missing files", list, "Remove them", width: 760, height: 520)) return;
            var result = await Task.Run(Editing.CleanupMissing);
            win.Toast($"Removed {result.FilesRemoved} files and {result.TitlesRemoved} titles.");
        });
        var repair = Ui.Button("Repair episode details", async () =>
        {
            var r = await Task.Run(() => Editing.RepairEpisodes());
            win.Toast($"Reconnected {r.Relinked} files, merged {r.Merged} duplicates, fetched {r.Episodes} episodes across {r.Seasons} seasons.");
        });
        return Ui.Section("Library folders", Para(
            RootList(win, "library_roots", "Mixed folders"),
            RootList(win, "movie_roots", "Films only"),
            RootList(win, "show_roots", "Series only"),
            Ui.Actions(scan, rescan, missing, repair)));
    }

    // --------------------------------------------------------------- metadata
    static FrameworkElement Metadata(MainWindow win)
    {
        var tmdb = Entry("tmdb_api_key");
        var omdb = Entry("omdb_api_key");
        var language = Entry("language");
        var order = new TextBox { Text = string.Join(", ", Settings.List("provider_order")), Height = 36, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 0, 10, 0) };

        var status = new StackPanel();
        void DrawStatus()
        {
            status.Children.Clear();
            foreach (var s in WatchFlix.Core.Library.Metadata.ProviderStatus())
            {
                var colour = s.State == "ready" ? Theme.Good : s.State == "needs key" ? Theme.Lamp : Theme.Cert;
                // A 4xx is the service rejecting what was asked, not the service
                // being down — calling both "unreachable" sends you looking in the wrong place.
                var rejected = s.LastError.Contains("HTTP 4") && !s.LastError.Contains("HTTP 429");
                var note = s.State switch
                {
                    "cooling down" => $"{(rejected ? "rejected the request" : "unreachable")}, retrying in {Math.Ceiling(s.DownFor / 60.0)} min",
                    "needs key" => "needs key",
                    _ => "ready",
                };
                var row = new DockPanel { LastChildFill = false };
                var right = Ui.Mono(note, Theme.Dim);
                right.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(right, System.Windows.Controls.Dock.Right);
                row.Children.Add(right);
                var name = Ui.Text(s.Name, 15, Theme.Paper, FontWeights.Bold);
                name.VerticalAlignment = VerticalAlignment.Center;
                var supports = Ui.Mono(string.Join(" + ", s.Supports), Theme.Dim);
                supports.VerticalAlignment = VerticalAlignment.Center;
                supports.Margin = new Thickness(10, 0, 0, 0);
                row.Children.Add(Ui.Row(Ui.Dot(colour), name, supports));
                status.Children.Add(Ui.Box(row));
                if (s.LastError.Length > 0)
                    status.Children.Add(Ui.Mono(s.LastError, Theme.Dimmer).Margin(22, -4, 0, 10));
            }
            var reset = Ui.Button("Retry the ones that failed", () =>
            {
                WatchFlix.Core.Library.Metadata.ResetProviderHealth();
                DrawStatus();
            });
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            status.Children.Add(reset);
        }
        DrawStatus();

        void SaveOrder()
        {
            var names = order.Text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim().ToLowerInvariant()).Where(n => Settings.ProviderNames.Contains(n)).Distinct().ToList();
            foreach (var n in Settings.ProviderNames)
                if (!names.Contains(n)) names.Add(n);
            Settings.SetList("provider_order", names);
            order.Text = string.Join(", ", names);
        }
        order.LostFocus += (_, _) => SaveOrder();

        var franchiseOut = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var franchises = Ui.Button("Fetch franchises", async () =>
        {
            franchiseOut.Children.Clear();
            franchiseOut.Children.Add(Ui.Mono("Looking up franchises…", Theme.Dim));
            var result = await Task.Run(WatchFlix.Core.Library.Metadata.RefreshCollections);
            franchiseOut.Children.Clear();
            franchiseOut.Children.Add(Ui.Mono(result.Ok ? $"Checked {result.Checked} films; {result.Found} belong to a franchise."
                                                         : result.Log.FirstOrDefault() ?? "", result.Ok ? Theme.Dim : Theme.Cert));
        });
        franchises.HorizontalAlignment = HorizontalAlignment.Left;

        var save = Ui.Button("Save settings", () =>
        {
            Settings.Save(new Dictionary<string, JsonNode?>
            {
                ["tmdb_api_key"] = tmdb.Text.Trim(),
                ["omdb_api_key"] = omdb.Text.Trim(),
                ["language"] = language.Text.Trim().Length > 0 ? language.Text.Trim() : "en-US",
            });
            SaveOrder();
            WatchFlix.Core.Library.Metadata.ResetProviderHealth();
            DrawStatus();
        }, primary: true);
        var foot = new Border
        {
            BorderBrush = Theme.Rail, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 16, 0, 0), Margin = new Thickness(0, 20, 0, 0),
            Child = new DockPanel { LastChildFill = false, Children = { Docked(save, System.Windows.Controls.Dock.Right) } },
        };

        return Ui.Section("Metadata providers", Para(
            Ui.FieldRow(
                Ui.Field("TMDB API key", tmdb),
                Ui.Field("OMDb API key", omdb),
                Ui.Field("Language", language)),
            Ui.Field("Provider order", order),
            Ui.Column(
                Tick("Scrape new titles during a scan", "auto_scrape_on_scan", true),
                Tick("Keep local copies of artwork", "download_images", true)).Margin(0, 0, 0, 14),
            status,
            Ui.Field("Franchise data", Ui.Column(franchises, franchiseOut)).Margin(0, 18, 0, 0),
            foot));
    }

    static T Docked<T>(T element, System.Windows.Controls.Dock side) where T : UIElement
    {
        DockPanel.SetDock(element, side);
        return element;
    }

    // --------------------------------------------------------------- previews
    static FrameworkElement Previews(MainWindow win)
    {
        var quality = Ui.Select(new[]
        {
            ("off", "Off"), ("low", "Low — 320px"), ("medium", "Medium — 480px"),
            ("high", "High — 720px"), ("ultra", "Ultra — 1080px"),
        }, MediaUtil.PreviewSettings().Name, key => Settings.Set("preview_quality", key), double.NaN);
        quality.HorizontalAlignment = HorizontalAlignment.Stretch;
        quality.Height = 36;
        var rebuild = Ui.Button("Rebuild existing previews", win.StartPreviewRebuild);
        return Ui.Section("Hover previews", Para(
            Ui.Field("Preview quality", quality),
            Ui.Actions(rebuild)));
    }

    // ------------------------------------------------------------------ about
    static FrameworkElement About(MainWindow win)
    {
        var stats = Catalog.Stats();
        var lines = new[]
        {
            $"{stats.Long("movies")} films · {stats.Long("shows")} series · {stats.Long("files")} files · " +
            $"{Ui.Bytes(stats.Double("bytes") ?? 0)} · {Ui.Runtime(stats.Double("seconds"))} of runtime",
            $"{stats.Long("people")} people · {stats.Long("unscraped")} unscraped · {stats.Long("missing")} missing",
            "Data folder: " + Config.AppHome,
            Ffmpeg.Available ? "ffmpeg: found" : "ffmpeg: not found",
        };
        var text = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var l in lines) text.Children.Add(Ui.Mono(l, Theme.Dim).Margin(0, 0, 0, 6));
        var openData = Ui.Button("Open data folder", () => Pages.ShowInFolder(Config.AppHome));
        var openLog = Ui.Button("Open log", () =>
        {
            if (File.Exists(Config.LogPath)) Pages.OpenUrl(Config.LogPath);
            else win.Toast("Nothing has been logged yet.");
        });
        return Ui.Section("Library", Para(text, Ui.Actions(openData, openLog)));
    }
}
