using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using WatchFlix.Core;
using WatchFlix.Core.Data;
using WatchFlix.Core.Library;
using WatchFlix.Core.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace WatchFlix.Desktop;

/// <summary>
/// The player. VLC does the playing, so every file plays as it is — HEVC,
/// 10-bit, MKV, any audio codec — with no converting and instant seeking.
///
/// A series gets its episode list beside the video; a film gets "Recommended
/// next": same franchise first, then films sharing its genres.
/// </summary>
public sealed class PlayerView : Grid
{
    const int Skip = 10;

    readonly MainWindow _win;
    LibVLC? _vlc;
    Task<bool>? _vlcStarting;
    readonly Border _opening = new();
    VlcMediaPlayer? _mp;
    VideoView? _view;
    Media? _media;

    // layout
    readonly Grid _left = new();
    readonly Border _videoHost = new() { Background = Brushes.Black };
    readonly Border _topBar = new();
    readonly Border _side = new();
    readonly ScrollViewer _info = new();
    readonly TextBlock _title = Ui.Text("", 17, Theme.Paper, FontWeights.SemiBold);
    readonly TextBlock _sub = Ui.Mono("", Theme.Dim, 12);
    readonly CheckBox _autoNext = new() { Content = Ui.Text("Auto next", 12.75, Theme.Paper), VerticalAlignment = VerticalAlignment.Center, Style = Theme.Style("Switch") };
    readonly ComboBox _audio = new() { Width = 190, Margin = new Thickness(10, 0, 0, 0), };
    readonly ComboBox _subs = new() { Width = 210, Margin = new Thickness(10, 0, 0, 0), };
    readonly Tracked _sideTitle = Ui.Caps("Up next", 11.1, Theme.Dim, 0.12, weight: FontWeights.ExtraBold);
    readonly ComboBox _sideSeason = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    readonly StackPanel _queueList = new();
    readonly ScrollViewer _queueScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

    // overlay controls, drawn over the video
    readonly Grid _overlay = new();
    readonly Border _controls = new();
    readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Focusable = false };
    readonly Slider _volume = new() { Minimum = 0, Maximum = 125, Width = 110, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _time = Ui.Mono("", Theme.Paper, 12.5);
    readonly Button _playButton;
    readonly Button _prevButton;
    readonly Button _nextButton;
    readonly Border _flash = new();
    readonly TextBlock _flashText = Ui.Text("", 22, Theme.Paper, FontWeights.SemiBold);
    readonly Border _overlayToast = new();
    readonly TextBlock _overlayToastText = Ui.Text("", 13.5, Theme.Paper, wrap: true);
    readonly TextBlock _overlayTitle = Ui.Text("", 18, Theme.Paper, FontWeights.SemiBold);

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };

    // what is playing
    long _fileId;
    double _duration;
    Catalog.Queue? _queue;
    int _index = -1;
    long? _seasonFilter;
    bool _seeking;
    bool _advancing;
    bool _tracksApplied;
    bool _fullscreen;
    WindowState _restoreState;
    WindowStyle _restoreStyle;
    TaskCompletionSource<bool>? _viewReady;
    bool _updatingCombos;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Playing somewhere: in the main window or the pop-out.</summary>
    public bool Active => IsOpen || _pop != null;

    public PlayerView(MainWindow win)
    {
        _win = win;
        Background = Theme.Vault;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });

        _playButton = TransportButton(Glyphs.Pause(), "Play", TogglePause);
        _prevButton = TransportButton(Glyphs.Previous(), "Previous", () => _ = GoTo(-1));
        _nextButton = TransportButton(Glyphs.Next(), "Next", () => _ = GoTo(1));

        BuildLeft();
        BuildSide();
        BuildOverlay();

        _autoNext.IsChecked = Settings.Bool("auto_next");
        _autoNext.Checked += (_, _) => Settings.Set("auto_next", true);
        _autoNext.Unchecked += (_, _) => Settings.Set("auto_next", false);

        _tick.Tick += (_, _) => UpdateClock();
        _saveTimer.Tick += (_, _) => SaveProgress();
        _hideTimer.Tick += (_, _) => HideControls();
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); _flash.Visibility = Visibility.Collapsed; };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _overlayToast.Visibility = Visibility.Collapsed; };
    }

    static Button TransportButton(UIElement glyph, string tip, Action action)
    {
        var b = new Button
        {
            Content = glyph,
            VerticalContentAlignment = VerticalAlignment.Center,
            Width = 42,
            Height = 36,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            BorderThickness = new Thickness(0),
            ToolTip = tip,
            Focusable = false,
        };
        b.Click += (_, _) => action();
        return b;
    }

    // ---------------------------------------------------------------- layout
    void BuildLeft()
    {
        _left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(250) });

        _topBar.Padding = new Thickness(22, 10, 16, 10);
        _topBar.BorderBrush = Theme.Rail;
        _topBar.BorderThickness = new Thickness(0, 0, 0, 1);
        var top = new DockPanel { LastChildFill = true };
        var close = Ui.Button("Close", () => _ = CloseAsync());
        close.Margin = new Thickness(10, 0, 0, 0);
        // Subtitles and audio tracks live behind the CC button beside the volume.
        var right = Ui.Row(_autoNext, close);
        right.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(right);
        top.Children.Add(Ui.Column(_title, _sub.Margin(0, 3, 0, 0)));
        _topBar.Child = top;
        _left.Children.Add(_topBar);

        Grid.SetRow(_videoHost, 1);
        _left.Children.Add(_videoHost);

        _info.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _info.Focusable = false;
        _info.Padding = new Thickness(26, 16, 26, 16);
        Grid.SetRow(_info, 2);
        _left.Children.Add(_info);
        Children.Add(_left);

        _subs.SelectionChanged += (_, _) => DrawCc();
        _audio.SelectionChanged += (_, _) =>
        {
            if (_updatingCombos || _mp == null || _audio.SelectedItem is not ComboBoxItem { Tag: int id }) return;
            _mp.SetAudioTrack(id);
            Settings.Set("audio_lang", LanguageOf((string)((ComboBoxItem)_audio.SelectedItem).Content));
        };
        _subs.SelectionChanged += (_, _) =>
        {
            if (_updatingCombos || _mp == null || _subs.SelectedItem is not ComboBoxItem { Tag: int id }) return;
            _mp.SetSpu(id);
            Settings.Set("sub_lang", id < 0 ? "off" : LanguageOf((string)((ComboBoxItem)_subs.SelectedItem).Content));
        };
    }

    void BuildSide()
    {
        _side.Background = Theme.Vault;
        _side.BorderBrush = Theme.Rail;
        _side.BorderThickness = new Thickness(1, 0, 0, 0);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _sideTitle.HorizontalAlignment = HorizontalAlignment.Left;
        var head = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            BorderBrush = Theme.Rail,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Ui.Column(_sideTitle, _sideSeason),
        };
        grid.Children.Add(head);
        _queueList.Margin = new Thickness(8);
        _queueScroll.Content = _queueList;
        Grid.SetRow(_queueScroll, 1);
        grid.Children.Add(_queueScroll);
        _side.Child = grid;
        Grid.SetColumn(_side, 1);
        Children.Add(_side);
        _sideSeason.SelectionChanged += (_, _) =>
        {
            if (_updatingCombos || _sideSeason.SelectedItem is not ComboBoxItem { Tag: long n }) return;
            _seasonFilter = n;
            DrawQueue();
        };
    }

    void BuildOverlay()
    {
        // Nearly transparent rather than transparent: a fully clear window lets
        // the mouse fall through, and then moving it would not show the controls.
        _overlay.Background = Theme.HitTarget;
        _overlay.MouseMove += (_, _) => ShowControls();
        _overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource != _overlay) return;
            if (e.ClickCount == 2) ToggleFullscreen();
            else TogglePause();
        };

        _overlayTitle.Margin = new Thickness(26, 20, 0, 0);
        _overlayTitle.VerticalAlignment = VerticalAlignment.Top;
        _overlayTitle.HorizontalAlignment = HorizontalAlignment.Left;
        _overlayTitle.Visibility = Visibility.Collapsed;
        _overlayTitle.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.9 };
        _overlay.Children.Add(_overlayTitle);

        _flash.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x13, 0x18));
        _flash.CornerRadius = new CornerRadius(6);
        _flash.Padding = new Thickness(22, 12, 22, 12);
        _flash.HorizontalAlignment = HorizontalAlignment.Center;
        _flash.VerticalAlignment = VerticalAlignment.Center;
        _flash.Visibility = Visibility.Collapsed;
        _flash.IsHitTestVisible = false;
        _flash.Child = _flashText;
        _overlay.Children.Add(_flash);

        _opening.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x13, 0x18));
        _opening.CornerRadius = new CornerRadius(3);
        _opening.Padding = new Thickness(18, 10, 18, 10);
        _opening.HorizontalAlignment = HorizontalAlignment.Center;
        _opening.VerticalAlignment = VerticalAlignment.Center;
        _opening.IsHitTestVisible = false;
        _opening.Visibility = Visibility.Collapsed;
        _opening.Child = Ui.Mono("Opening…", Theme.Dim, 12);
        _overlay.Children.Add(_opening);

        _overlayToast.Background = Theme.Rail;
        _overlayToast.BorderBrush = Theme.Edge;
        _overlayToast.BorderThickness = new Thickness(1);
        _overlayToast.CornerRadius = new CornerRadius(4);
        _overlayToast.Padding = new Thickness(16, 10, 16, 10);
        _overlayToast.MaxWidth = 520;
        _overlayToast.HorizontalAlignment = HorizontalAlignment.Center;
        _overlayToast.VerticalAlignment = VerticalAlignment.Top;
        _overlayToast.Margin = new Thickness(0, 22, 0, 0);
        _overlayToast.Visibility = Visibility.Collapsed;
        _overlayToast.IsHitTestVisible = false;
        _overlayToast.Child = _overlayToastText;
        _overlay.Children.Add(_overlayToast);

        _controls.VerticalAlignment = VerticalAlignment.Bottom;
        _controls.Padding = new Thickness(20, 28, 20, 12);
        _controls.Background = new LinearGradientBrush(Color.FromArgb(0x00, 0, 0, 0), Color.FromArgb(0xD8, 0x08, 0x0A, 0x0D), 90);
        var stack = new StackPanel();
        _seek.Margin = new Thickness(0, 0, 0, 6);
        _seek.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        _seek.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _seeking = false;
            SeekTo(_seek.Value);
        };
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _seeking = false;
            SeekTo(_seek.Value);
        }));
        stack.Children.Add(_seek);

        var row = new DockPanel { LastChildFill = false };
        var back = TransportButton(Glyphs.Skip(forward: false), "Back 10", () => SkipBy(-Skip));
        var fwd = TransportButton(Glyphs.Skip(forward: true), "Forward 10", () => SkipBy(Skip));
        _time.VerticalAlignment = VerticalAlignment.Center;
        _time.Margin = new Thickness(10, 0, 0, 0);
        var leftGroup = Ui.Row(_prevButton, back, _playButton, fwd, _nextButton, _time);
        row.Children.Add(leftGroup);

        _muteButton = TransportButton(Glyphs.Speaker(false), "Mute", ToggleMute);
        _volume.Value = Math.Clamp(Settings.Int("volume", 100), 0, 125);
        _volume.ValueChanged += (_, _) =>
        {
            // Moving the slider while muted means you want to hear it.
            if (_muted && _volume.Value > 0) _muted = false;
            ApplyVolume();
            Settings.Set("volume", (int)_volume.Value);
        };
        var mute = _muteButton;
        DrawMute();
        _ccButton = TransportButton(Glyphs.Captions(false), "Subtitles", () => OpenTrackMenu(_ccButton!));
        _ccButton.Margin = new Thickness(10, 0, 0, 0);
        var popOut = TransportButton(Glyphs.PopOut(), "Pop out", () => _ = PopOut());
        var full = TransportButton(Glyphs.FullScreen(), "Full screen", ToggleFullscreen);
        var rightGroup = Ui.Row(mute, _volume, _ccButton, popOut, full);
        DockPanel.SetDock(rightGroup, Dock.Right);
        row.Children.Add(rightGroup);
        stack.Children.Add(row);
        _controls.Child = stack;
        _controls.MouseEnter += (_, _) => _hideTimer.Stop();
        _controls.MouseLeave += (_, _) => RestartHideTimer();
        _overlay.Children.Add(_controls);
    }

    // ------------------------------------------------------------ VLC set-up
    /// <summary>
    /// VLC loads a few hundred plugins when it starts, which takes seconds on a
    /// cold disk. That happens on a worker thread — once, shared by everyone who
    /// asks — so the window never stops responding while it does. The main
    /// window starts it quietly a moment after launch, so the first Play is quick.
    /// </summary>
    public Task<bool> EnsureVlcAsync() => _vlcStarting ??= StartVlcAsync();

    async Task<bool> StartVlcAsync()
    {
        var volume = EffectiveVolume;
        try
        {
            var (vlc, mp) = await Task.Run(() =>
            {
                LibVLCSharp.Shared.Core.Initialize();
                var v = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--avcodec-hw=any");
                var m = new VlcMediaPlayer(v)
                {
                    EnableHardwareDecoding = true,
                    EnableKeyInput = false,
                    EnableMouseInput = false,
                    Volume = volume,
                };
                return (v, m);
            });
            _vlc = vlc;
            _mp = mp;
            _mp.Playing += (_, _) => Dispatcher.BeginInvoke(OnPlaying);
            _mp.Paused += (_, _) => Dispatcher.BeginInvoke(() => SetPlayGlyph(false));
            _mp.EndReached += (_, _) => Dispatcher.BeginInvoke(() => _ = OnEnded());
            _mp.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                ShowOpening(false);
                Notify("This file could not be played.", true);
            });
            _mp.LengthChanged += (_, e) => Dispatcher.BeginInvoke(() =>
            {
                if (e.Length > 0) _duration = e.Length / 1000.0;
            });
            _mp.ESAdded += (_, _) => Dispatcher.BeginInvoke(() => RefreshTracks(false));
            return true;
        }
        catch (Exception ex)
        {
            App.Log("VLC failed to start: " + ex);
            _win.Toast("The video player could not start: " + ex.Message, true);
            _vlc = null;
            _mp = null;
            _vlcStarting = null;        // let the next Play try again
            return false;
        }
    }

    /// <summary>"Opening…" in the middle of the picture until the first frame arrives.</summary>
    void ShowOpening(bool on) => _opening.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// A fresh video surface each time the player opens. The overlay that
    /// carries the controls is its own window and only hides when the surface
    /// leaves the screen, so the surface comes and goes with the player.
    /// </summary>
    Task EnsureView()
    {
        if (_view != null) return _viewReady?.Task ?? Task.CompletedTask;
        _viewReady = new TaskCompletionSource<bool>();
        var ready = _viewReady;
        DetachOverlay();
        var view = new VideoView { Background = Brushes.Black, Content = _overlay };
        view.Loaded += (_, _) =>
        {
            view.MediaPlayer = _mp;
            ready.TrySetResult(true);
        };
        _view = view;
        _videoHost.Child = view;
        return ready.Task;
    }

    void DropView()
    {
        var view = _view;
        _view = null;
        _videoHost.Child = null;
        if (view == null) return;
        try
        {
            view.MediaPlayer = null;
            view.Dispose();
        }
        catch (Exception ex) { App.Log("Video surface: " + ex.Message); }
        DetachOverlay();
    }

    /// <summary>The controls move from one surface's overlay window to the next.</summary>
    void DetachOverlay()
    {
        switch (_overlay.Parent)
        {
            case Panel panel: panel.Children.Remove(_overlay); break;
            case ContentControl host: host.Content = null; break;
            case Decorator decorator: decorator.Child = null; break;
        }
    }

    // --------------------------------------------------------------- opening
    public async Task OpenAsync(long fileId, double position = 0)
    {
        if (_pop != null)
        {
            // Something new chosen in the library: it opens in the main player.
            var playing = _mp;
            if (playing != null) await Task.Run(() => playing.Stop());
            ClosePop();
        }
        Visibility = Visibility.Visible;
        _win.UpdateBack();
        if (_vlc == null)
        {
            var starting = Ui.Mono("Starting the video player…", Theme.Dim, 12);
            starting.HorizontalAlignment = HorizontalAlignment.Center;
            starting.VerticalAlignment = VerticalAlignment.Center;
            _videoHost.Child = starting;
        }
        if (!await EnsureVlcAsync())
        {
            _videoHost.Child = null;
            Visibility = Visibility.Collapsed;
            _win.UpdateBack();
            return;
        }
        if (!IsOpen) return;                 // closed while the player was starting
        if (_view == null) _videoHost.Child = null;
        await EnsureView();
        _queue = await Task.Run(() => Catalog.QueueFor(fileId));
        _seasonFilter = null;
        await PlayFile(fileId, position);
        Focus();
    }

    async Task PlayFile(long fileId, double position)
    {
        if (_mp == null || _vlc == null) return;
        var info = await Task.Run(() => Catalog.FileDetail(fileId));
        if (info == null)
        {
            _win.Toast("That file is no longer in the library.", true);
            await CloseAsync();
            return;
        }
        var path = info.Str("path");
        if (!File.Exists(path))
        {
            _win.Toast("File not found.", true);
            await CloseAsync();
            return;
        }
        SaveProgress();
        _fileId = fileId;
        _advancing = false;
        _tracksApplied = false;
        _duration = info.Double("duration") ?? 0;

        // A recommendation starts a different film, which has its own queue.
        _index = _queue?.Items.FindIndex(i => i.Long("file_id") == fileId) ?? -1;
        if (_index < 0 || (_queue!.Kind == "movie" && _queue.Items[_index].Long("media_id") != _queue.Media.Long("id")))
        {
            _queue = await Task.Run(() => Catalog.QueueFor(fileId));
            _index = _queue?.Items.FindIndex(i => i.Long("file_id") == fileId) ?? -1;
        }
        var item = _index >= 0 ? _queue!.Items[_index] : null;
        var media = _queue?.Media ?? new Row();
        var isShow = _queue?.Kind == "show";

        _title.Text = media.Truthy("title") ? media.Str("title") : info.Str("filename");
        var subBits = isShow
            ? new[] { item?.Str("label") ?? "", item?.Str("title") ?? "", info.Str("quality"), Ui.Runtime(_duration) }
            : new[] { media.Str("year"), string.Join(", ", Ui.Strings(media.Get("genres")).Take(2)), media.Str("certificate"),
                      info.Str("quality"), Ui.Runtime(_duration) };
        _sub.Text = string.Join(" · ", subBits.Where(b => b.Length > 0));
        _overlayTitle.Text = isShow && item != null ? $"{_title.Text}  ·  {item.Str("label")}  {item.Str("title")}" : _title.Text;
        _pop?.SetTitle(PopTitle(item));

        _media?.Dispose();
        _media = new Media(_vlc, path, FromType.FromPath);
        if (position > 5 && (_duration <= 0 || position < _duration - 10))
            _media.AddOption($":start-time={position.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
        // Play swaps out whatever was playing, and stopping a file can take a
        // moment inside VLC — so it is asked for off the UI thread.
        ShowOpening(true);
        var mp = _mp;
        var clip = _media;
        var volume = EffectiveVolume;
        await Task.Run(() =>
        {
            mp.Mute = false;
            mp.Volume = volume;
            mp.Play(clip);
        });

        var id = fileId;
        _ = Task.Run(() => Catalog.MarkStarted(id));
        DrawTransport();
        DrawQueue();
        DrawInfo(item, info);
        _audio.Visibility = Visibility.Collapsed;
        _subs.Items.Clear();
        _subs.Items.Add(new ComboBoxItem { Content = "Subtitles off", Tag = -1 });
        _subs.SelectedIndex = 0;
        _tick.Start();
        _saveTimer.Start();
        ShowControls();
    }

    void OnPlaying()
    {
        ShowOpening(false);
        SetPlayGlyph(true);
        DrawMute();
        SyncAudio();
        if (_pauseWhenPlaying)
        {
            // Moved between windows while paused: stay paused.
            _pauseWhenPlaying = false;
            _mp?.SetPause(true);
        }
        if (_mp != null && _mp.Length > 0) _duration = _mp.Length / 1000.0;
        RefreshTracks(true);
        RestartHideTimer();
    }

    // ----------------------------------------------------- audio and subtitles
    static readonly Regex Bracketed = new(@"\[([^\]]+)\]", RegexOptions.CultureInvariant);

    /// <summary>"Track 2 - [Japanese]" → "Japanese". Remembered by language, since track numbers differ file to file.</summary>
    static string LanguageOf(string name)
    {
        var m = Bracketed.Match(name);
        var lang = m.Success ? m.Groups[1].Value : name;
        return lang.Trim().ToLowerInvariant();
    }

    static string TidyTrackName(string name)
    {
        // VLC names tracks "Track 1 - [English]" or "English - SDH - [English]".
        var m = Bracketed.Match(name);
        var lang = m.Success ? m.Groups[1].Value.Trim() : "";
        var head = Bracketed.Replace(name, "").Trim(' ', '-');
        if (Regex.IsMatch(head, @"^Track \d+$") && lang.Length > 0) return lang;
        if (lang.Length > 0 && !head.Contains(lang, StringComparison.OrdinalIgnoreCase)) return $"{lang} · {head}";
        return head.Length > 0 ? head : name;
    }

    void RefreshTracks(bool applyPreferences)
    {
        if (_mp == null) return;
        _updatingCombos = true;
        try
        {
            var audio = _mp.AudioTrackDescription.Where(t => t.Id >= 0).ToList();
            _audio.Items.Clear();
            foreach (var t in audio) _audio.Items.Add(new ComboBoxItem { Content = TidyTrackName(t.Name ?? $"Track {t.Id}"), Tag = t.Id });
            _audio.Visibility = audio.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            SelectByTag(_audio, _mp.AudioTrack);

            var subs = _mp.SpuDescription.Where(t => t.Id >= 0).ToList();
            _subs.Items.Clear();
            _subs.Items.Add(new ComboBoxItem { Content = "Subtitles off", Tag = -1 });
            foreach (var t in subs) _subs.Items.Add(new ComboBoxItem { Content = TidyTrackName(t.Name ?? $"Track {t.Id}"), Tag = t.Id });
            SelectByTag(_subs, _mp.Spu);
        }
        finally
        {
            _updatingCombos = false;
        }

        if (!applyPreferences || _tracksApplied) return;
        _tracksApplied = true;
        // Tracks trickle in just after playback starts, so choose a moment later.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        var fileId = _fileId;
        var attempts = 0;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (fileId != _fileId || _mp == null) return;
            RefreshTracks(false);
            if (_carryTracks is { } carry && carry.FileId == fileId)
            {
                // Tracks can take a moment longer to appear; wait for the one wanted.
                bool Listed(ComboBox box, int id) => box.Items.OfType<ComboBoxItem>().Any(i => i.Tag is int t && t == id);
                var ready = (carry.Spu < 0 || Listed(_subs, carry.Spu)) && (carry.Audio < 0 || _audio.Items.Count == 0 || Listed(_audio, carry.Audio));
                if (!ready && ++attempts < 10)
                {
                    timer.Start();
                    return;
                }
                _carryTracks = null;
                _updatingCombos = true;
                try
                {
                    if (carry.Audio >= 0 && Listed(_audio, carry.Audio))
                    {
                        _mp.SetAudioTrack(carry.Audio);
                        SelectByTag(_audio, carry.Audio);
                    }
                    _mp.SetSpu(carry.Spu >= 0 && Listed(_subs, carry.Spu) ? carry.Spu : -1);
                    SelectByTag(_subs, carry.Spu >= 0 && Listed(_subs, carry.Spu) ? carry.Spu : -1);
                }
                finally { _updatingCombos = false; }
                return;
            }
            ApplyPreference(_audio, Settings.Str("audio_lang"), isSubs: false);
            ApplyPreference(_subs, Settings.Str("sub_lang", "english"), isSubs: true);
        };
        timer.Start();
    }

    void ApplyPreference(ComboBox box, string wanted, bool isSubs)
    {
        if (_mp == null || wanted.Length == 0) return;
        var items = box.Items.OfType<ComboBoxItem>().ToList();
        if (isSubs && wanted == "off")
        {
            _mp.SetSpu(-1);
            _updatingCombos = true;
            SelectByTag(box, -1);
            _updatingCombos = false;
            return;
        }
        var match = items.Where(i => i.Tag is int id && id >= 0)
            .Where(i => ((string)i.Content).ToLowerInvariant().Contains(wanted))
            .OrderBy(i => ((string)i.Content).ToLowerInvariant().Contains("forced") ? 1 : 0)
            .ThenBy(i => ((string)i.Content).ToLowerInvariant().Contains("sdh") ? 1 : 0)
            .FirstOrDefault();
        if (match == null) return;
        var tag = (int)match.Tag;
        if (isSubs) _mp.SetSpu(tag);
        else _mp.SetAudioTrack(tag);
        _updatingCombos = true;
        box.SelectedItem = match;
        _updatingCombos = false;
    }

    static void SelectByTag(ComboBox box, int tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (item.Tag is int t && t == tag)
            {
                box.SelectedItem = item;
                return;
            }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    void Cycle(ComboBox box)
    {
        if (box.Items.Count < 2) return;
        box.SelectedIndex = (box.SelectedIndex + 1) % box.Items.Count;
        if (box.SelectedItem is ComboBoxItem item) Notify((string)item.Content);
    }

    // -------------------------------------------------------------- transport
    public void TogglePause()
    {
        if (_mp == null) return;
        if (_mp.IsPlaying) { _mp.SetPause(true); Flash("❚❚"); }
        else
        {
            if (_mp.State == VLCState.Ended && _media != null)
            {
                var mp = _mp;
                var media = _media;
                _ = Task.Run(() => mp.Play(media));
            }
            else _mp.SetPause(false);
            Flash("▶");
        }
    }

    void SetPlayGlyph(bool playing)
    {
        _playButton.Content = playing ? Glyphs.Pause() : Glyphs.Play();
        _pop?.SetPlaying(playing);
        if (!playing) ShowControls(stay: true);
    }

    void SkipBy(int seconds)
    {
        if (_mp == null) return;
        SeekTo(Now() + seconds);
        Flash(seconds > 0 ? $"+{seconds}s" : $"{seconds}s");
    }

    double Now() => _mp != null ? Math.Max(0, _mp.Time / 1000.0) : 0;

    void SeekTo(double seconds)
    {
        if (_mp == null) return;
        var total = _duration > 0 ? _duration : _mp.Length / 1000.0;
        var target = Math.Clamp(seconds, 0, Math.Max(0, total - 1));
        _mp.Time = (long)(target * 1000);
        UpdateClock();
    }

    // Muting is done as volume 0 rather than VLC's own mute switch, which VLC
    // quietly resets whenever a new file opens.
    bool _muted;
    Button? _muteButton;

    void ToggleMute()
    {
        _muted = !_muted;
        ApplyVolume();
        Flash(_muted ? "Muted" : "Sound on");
    }

    int EffectiveVolume => _muted ? 0 : (int)_volume.Value;

    void ApplyVolume()
    {
        if (_mp != null)
        {
            _mp.Mute = false;
            _mp.Volume = EffectiveVolume;
        }
        DrawMute();
    }

    /// <summary>
    /// VLC's volume and mute are Windows' own per-app sound setting for
    /// WatchFlix, which Windows remembers between runs — so a mute pressed in
    /// an earlier version stayed on, and no sound came out. Once a file's sound
    /// has actually started, the app's own setting is put back: sound on, at
    /// the slider's level. It is repeated briefly because the sound device can
    /// take a moment to open.
    /// </summary>
    async void SyncAudio()
    {
        foreach (var wait in new[] { 0, 500, 1500 })
        {
            if (wait > 0) await Task.Delay(wait);
            var mp = _mp;
            if (mp == null || !Active) return;
            var volume = EffectiveVolume;
            try
            {
                await Task.Run(() =>
                {
                    mp.Mute = false;
                    mp.Volume = volume;
                });
            }
            catch (Exception ex) { App.Log("Sound: " + ex.Message); }
        }
    }

    /// <summary>The speaker shows sound waves, or a cross when muted; the slider greys out with it.</summary>
    void DrawMute()
    {
        var silent = _muted || _volume.Value <= 0;
        _volume.Opacity = _muted ? 0.35 : 1;
        if (_muteButton == null) return;
        var icon = Glyphs.Speaker(silent);
        _muteButton.Content = icon;
        _pop?.SetMuted(silent);
        _muteButton.ToolTip = _muted ? "Unmute" : "Mute";
    }

    void UpdateClock()
    {
        if (_mp == null) return;
        var total = _duration > 0 ? _duration : _mp.Length / 1000.0;
        var now = Now();
        if (!_seeking)
        {
            _seek.Maximum = Math.Max(1, total);
            _seek.Value = Math.Min(now, _seek.Maximum);
        }
        _time.Text = total > 0 ? $"{Ui.Clock(_seeking ? _seek.Value : now)} / {Ui.Clock(total)}" : "";
        _pop?.SetTime(now, total);
    }

    void Flash(string text)
    {
        _flashText.Text = text;
        _flash.Visibility = Visibility.Visible;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    /// <summary>Messages show inside the video too, where the main window's would be hidden.</summary>
    public void MirrorToast(string message, bool bad)
    {
        if (!IsOpen) return;
        _overlayToastText.Text = message;
        _overlayToast.BorderBrush = bad ? Theme.Cert : Theme.Edge;
        _overlayToast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void Notify(string message, bool bad = false) => _win.Toast(message, bad);

    void ShowControls(bool stay = false)
    {
        _controls.Visibility = Visibility.Visible;
        _overlay.Cursor = null;
        if (_fullscreen) _overlayTitle.Visibility = Visibility.Visible;
        if (stay) _hideTimer.Stop();
        else RestartHideTimer();
    }

    void RestartHideTimer()
    {
        _hideTimer.Stop();
        if (_mp?.IsPlaying == true) _hideTimer.Start();
    }

    void HideControls()
    {
        _hideTimer.Stop();
        if (_mp?.IsPlaying != true || _controls.IsMouseOver || _seeking || MenuOpen) return;
        _controls.Visibility = Visibility.Collapsed;
        _overlayTitle.Visibility = Visibility.Collapsed;
        _overlay.Cursor = Cursors.None;
    }

    public void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _restoreState = _win.WindowState;
            _restoreStyle = _win.WindowStyle;
            _win.WindowStyle = WindowStyle.None;
            if (_win.WindowState == WindowState.Maximized) _win.WindowState = WindowState.Normal;
            _win.WindowState = WindowState.Maximized;
            _topBar.Visibility = Visibility.Collapsed;
            _info.Visibility = Visibility.Collapsed;
            _side.Visibility = Visibility.Collapsed;
            _left.RowDefinitions[2].Height = new GridLength(0);
            Grid.SetColumnSpan(_left, 2);
        }
        else
        {
            _win.WindowStyle = _restoreStyle;
            _win.WindowState = WindowState.Normal;
            _win.WindowState = _restoreState;
            _topBar.Visibility = Visibility.Visible;
            _info.Visibility = Visibility.Visible;
            _side.Visibility = Visibility.Visible;
            _left.RowDefinitions[2].Height = new GridLength(250);
            Grid.SetColumnSpan(_left, 1);
            _overlayTitle.Visibility = Visibility.Collapsed;
        }
        ShowControls();
    }

    // ------------------------------------------------------------ the queue
    Row? Neighbour(int step)
    {
        if (_queue == null) return null;
        var i = _index + step;
        return i >= 0 && i < _queue.Items.Count ? _queue.Items[i] : null;
    }

    async Task GoTo(int step)
    {
        var target = Neighbour(step);
        if (target == null) return;
        await PlayFile(target.Long("file_id") ?? 0, target.Truthy("completed") ? 0 : target.Double("position") ?? 0);
    }

    void DrawTransport()
    {
        var isShow = _queue?.Kind == "show";
        var prev = Neighbour(-1);
        var next = Neighbour(1);
        _prevButton.IsEnabled = prev != null;
        _nextButton.IsEnabled = next != null;
        _prevButton.ToolTip = prev != null ? $"Previous: {prev.Str("title")}" : null;
        _nextButton.ToolTip = next != null ? $"Next: {next.Str("title")}" : null;
    }

    void DrawQueue()
    {
        _queueList.Children.Clear();
        var isShow = _queue?.Kind == "show";
        _sideTitle.Set((isShow ? "Episodes" : "Recommended next").ToUpperInvariant(), Theme.Dim, FontWeights.ExtraBold);
        var items = _queue?.Items ?? new List<Row>();
        if (items.Count == 0 || (!isShow && items.Count < 2))
        {
            _sideSeason.Visibility = Visibility.Collapsed;
            return;
        }
        var shown = items;
        if (isShow)
        {
            var seasons = items.Select(i => i.Long("season")).Where(n => n != null).Select(n => n!.Value).Distinct().ToList();
            if (seasons.Count > 1)
            {
                _seasonFilter ??= items.ElementAtOrDefault(_index)?.Long("season") ?? seasons[0];
                _updatingCombos = true;
                _sideSeason.Items.Clear();
                foreach (var n in seasons)
                {
                    var count = items.Count(i => i.Long("season") == n);
                    var entry = new ComboBoxItem { Content = $"{(n == 0 ? "Specials" : $"Season {n}")} · {count} ep", Tag = n };
                    _sideSeason.Items.Add(entry);
                    if (n == _seasonFilter) _sideSeason.SelectedItem = entry;
                }
                _updatingCombos = false;
                _sideSeason.Visibility = Visibility.Visible;
                shown = items.Where(i => i.Long("season") == _seasonFilter).ToList();
            }
            else _sideSeason.Visibility = Visibility.Collapsed;
        }
        else _sideSeason.Visibility = Visibility.Collapsed;

        FrameworkElement? current = null;
        foreach (var i in shown)
        {
            var on = i.Long("file_id") == _fileId;
            var element = QueueItem(i, on, isShow);
            if (on) current = element;
            _queueList.Children.Add(element);
        }
        if (current != null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => current.BringIntoView());
    }

    FrameworkElement QueueItem(Row i, bool on, bool isShow)
    {
        var art = i.Truthy("still") ? i.Str("still") : i.Str("thumb");
        var artGrid = new Grid();
        if (art.Length > 0) artGrid.Children.Add(Images.Lazy(art, 200));
        if (on)
        {
            var now = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x10, 0x13, 0x18)),
                Child = new Tracked { Family = Theme.Mono, Size = 9.3, Em = 0.1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    .Add("NOW PLAYING", Theme.Lamp),
            };
            artGrid.Children.Add(now);
        }
        else if ((i.Double("progress") ?? 0) > 0.01)
        {
            var bar = Ui.ProgressBar(i.Double("progress") ?? 0, 3);
            bar.VerticalAlignment = VerticalAlignment.Bottom;
            artGrid.Children.Add(bar);
        }
        var artBorder = new Border { Width = 96, Height = 54, Background = Theme.Rail, CornerRadius = new CornerRadius(2), ClipToBounds = true, Child = artGrid, VerticalAlignment = VerticalAlignment.Top };

        var kicker = isShow ? i.Str("label")
            : i.Str("reason") switch
            {
                "current" => "",
                "franchise" => i.Truthy("series") ? Regex.Replace(i.Str("series"), " Collection$", "", RegexOptions.IgnoreCase).ToUpperInvariant() : "SAME SERIES",
                _ => "SIMILAR",
            };
        var meta = new List<string>();
        if (!isShow) meta.Add(i.Str("label"));
        meta.Add(Ui.Runtime(i.Double("duration")));
        if (isShow) meta.Add(i.Str("quality"));
        if (i.Double("rating") is > 0) meta.Add("★ " + Ui.Rating(i.Double("rating")));
        if (i.Truthy("completed")) meta.Add("watched");
        var title = Ui.Text(i.Str("title"), 12.3, on ? Theme.Lamp : i.Truthy("completed") ? Theme.Dim : Theme.Paper, FontWeights.Bold,
                            wrap: true, margin: new Thickness(0, 2, 0, 3));
        title.LineHeight = 16;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        title.MaxHeight = 32;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = Ui.Column(
            Ui.Mono(kicker, !isShow && i.Str("reason") == "franchise" ? Theme.LampDim : Theme.Dimmer, 10.2),
            title,
            Ui.Mono(string.Join("  ·  ", meta.Where(m => m.Length > 0)), Theme.Dimmer, 9.9));
        text.Margin = new Thickness(10, 0, 0, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(artBorder);
        grid.Children.Add(text);
        var border = new Border
        {
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6),
            Background = on ? Theme.Shelf : Theme.Clear,
            BorderBrush = on ? Theme.Edge : Theme.Clear,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = grid,
        };
        var edge = new Border
        {
            Width = 3, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left,
            Background = on ? Theme.Lamp : Theme.Clear, CornerRadius = new CornerRadius(3, 0, 0, 3),
        };
        var host = new Grid();
        host.Children.Add(border);
        host.Children.Add(edge);
        var fileId = i.Long("file_id") ?? 0;
        var button = Ui.Bare(host, () =>
        {
            if (fileId == _fileId) return;
            _ = PlayFile(fileId, i.Truthy("completed") ? 0 : i.Double("position") ?? 0);
        });
        button.MouseEnter += (_, _) => { if (!on) { border.Background = Theme.Shelf; border.BorderBrush = Theme.Rail; } };
        button.MouseLeave += (_, _) => { if (!on) { border.Background = Theme.Clear; border.BorderBrush = Theme.Clear; } };
        return button;
    }

    // ----------------------------------------------------- under the video
    void DrawInfo(Row? item, Row info)
    {
        var isShow = _queue?.Kind == "show";
        var media = _queue?.Media ?? new Row();
        var stack = new StackPanel();
        var title = isShow ? (item?.Str("title") ?? info.Str("filename")) : media.Truthy("title") ? media.Str("title") : info.Str("filename");
        stack.Children.Add(Ui.Text(title, 18, Theme.Paper, FontWeights.SemiBold));
        if (!isShow && media.Truthy("tagline"))
            stack.Children.Add(Ui.Text(media.Str("tagline"), 13.5, Theme.Dim, wrap: true, margin: new Thickness(0, 2, 0, 0)));

        var facts = new WrapPanel { Margin = new Thickness(0, 10, 0, 4) };
        if (isShow && item != null && item.Truthy("label")) facts.Children.Add(Ui.Badge(item.Str("label")));
        if (!isShow && media.Truthy("certificate")) facts.Children.Add(Ui.Badge(media.Str("certificate"), Theme.Cert));
        var rating = isShow ? item?.Double("rating") : media.Double("rating");
        if (rating is > 0) facts.Children.Add(Ui.Badge(Ui.Rating(rating) + "/10", Theme.Lamp, new SolidColorBrush(Color.FromRgb(0x17, 0x11, 0x02))));
        var date = isShow ? item?.Str("air_date") ?? "" : media.Truthy("release_date") ? media.Str("release_date") : media.Str("year");
        if (date.Length > 0) facts.Children.Add(Ui.Badge(date));
        if (info.Truthy("quality")) facts.Children.Add(Ui.Badge(info.Str("quality")));
        if (_duration > 0) facts.Children.Add(Ui.Badge(Ui.Runtime(_duration)));
        if (!isShow)
            foreach (var g in Ui.Strings(media.Get("genres")))
            {
                var name = g;
                facts.Children.Add(Ui.GenreLink(name, () =>
                {
                    _ = CloseAsync();
                    Pages.ShowGenre(_win, "movie", name);
                }));
            }
        stack.Children.Add(facts);

        var synopsis = isShow ? item?.Str("overview") ?? "" : media.Str("overview");
        stack.Children.Add(synopsis.Length > 0
            ? Ui.Text(synopsis, 14, Theme.Paper, wrap: true, margin: new Thickness(0, 4, 0, 12))
            : Ui.Text($"No synopsis yet. Open {media.Str("title")} and scrape it to fill {(isShow ? "in per-episode details" : "this in")}.",
                      13, Theme.Dim, wrap: true, margin: new Thickness(0, 4, 0, 12)));
        var shots = new WrapPanel();
        shots.Children.Add(Ui.Mono("Grabbing frames…", Theme.Dimmer));
        stack.Children.Add(shots);
        _info.Content = stack;
        _info.ScrollToTop();

        var fileId = _fileId;
        var path = info.Str("path");
        var duration = _duration;
        _ = Task.Run(() => MediaUtil.MakeSnapshots(path, duration)).ContinueWith(t =>
        {
            if (fileId != _fileId) return;
            shots.Children.Clear();
            if (t.IsFaulted || t.Result.Count == 0)
            {
                shots.Children.Add(Ui.Mono(Ffmpeg.HasFfmpeg ? "" : "Install ffmpeg to see frames from the file.", Theme.Dimmer));
                return;
            }
            foreach (var shot in t.Result)
            {
                var at = shot.At;
                var picture = new Border
                {
                    Width = 200, Height = 112, CornerRadius = new CornerRadius(3), ClipToBounds = true, Background = Theme.Rail,
                    Child = Images.Lazy(shot.File, 400),
                };
                var label = Ui.Badge(Ui.Clock(at));
                label.HorizontalAlignment = HorizontalAlignment.Left;
                label.VerticalAlignment = VerticalAlignment.Bottom;
                label.Margin = new Thickness(5);
                var g = new Grid();
                g.Children.Add(picture);
                g.Children.Add(label);
                var b = Ui.Bare(g, () =>
                {
                    SeekTo(at);
                    if (_mp?.IsPlaying == false) _mp.SetPause(false);
                }, $"Jump to {Ui.Clock(at)}");
                b.Margin = new Thickness(0, 0, 12, 8);
                shots.Children.Add(b);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void FrameAsPoster()
    {
        var fileId = _fileId;
        var at = Now();
        _ = Task.Run(() => Editing.FrameAsPoster(fileId, at)).ContinueWith(t =>
        {
            Images.Clear();
            if (t.IsFaulted) Notify("That frame could not be grabbed.", true);
            else Notify(t.Result.Message, !t.Result.Ok);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ------------------------------------------------------ progress, ending
    void SaveProgress()
    {
        if (_mp == null || _fileId == 0) return;
        var total = _duration > 0 ? _duration : _mp.Length / 1000.0;
        if (total <= 0) return;
        var at = _mp.State == VLCState.Ended ? total : Now();
        if (at <= 0) return;
        var fileId = _fileId;
        _ = Task.Run(() =>
        {
            try { Catalog.SaveProgress(fileId, at, total); }
            catch (Exception ex) { App.Log("Saving progress: " + ex.Message); }
        });
        var entry = _queue?.Items.FirstOrDefault(i => i.Long("file_id") == fileId);
        if (entry != null)
        {
            entry["position"] = at;
            entry["progress"] = at / total;
            entry["completed"] = at / total >= Config.CompleteAt ? 1L : 0L;
        }
    }

    async Task OnEnded()
    {
        if (_advancing) return;
        _advancing = true;
        SetPlayGlyph(false);
        SaveProgress();
        DrawQueue();
        var next = Neighbour(1);
        if (_autoNext.IsChecked == true && next != null)
        {
            Notify($"Next up: {(next.Truthy("label") && _queue?.Kind == "show" ? next.Str("label") : next.Str("title"))}");
            await PlayFile(next.Long("file_id") ?? 0, 0);
        }
        else
        {
            _advancing = false;
        }
    }

    public async Task CloseAsync()
    {
        if (!Active) return;
        if (_fullscreen) ToggleFullscreen();
        SaveProgress();
        _tick.Stop();
        _saveTimer.Stop();
        _hideTimer.Stop();
        ShowOpening(false);
        var mp = _mp;
        if (mp != null) await Task.Run(() => mp.Stop());
        DropView();
        ClosePop();
        _media?.Dispose();
        _media = null;
        var mediaId = _queue?.Media.Long("id");
        _fileId = 0;
        _queue = null;
        Visibility = Visibility.Collapsed;
        _win.UpdateBack();
        // The page underneath shows where you got to.
        if (mediaId != null && _win.Here.Kind is "movie" or "show" or "home" or "movies" or "shows") _win.Refresh();
    }

    public void Shutdown()
    {
        try
        {
            SaveProgress();
            _mp?.Stop();
            DropView();
            ClosePop();
            _media?.Dispose();
            _mp?.Dispose();
            _vlc?.Dispose();
        }
        catch { }
    }

    // ------------------------------------------------- subtitles and audio menu
    Button? _ccButton;
    public bool MenuOpen { get; private set; }

    void DrawCc()
    {
        var on = _subs.SelectedItem is ComboBoxItem { Tag: int id } && id >= 0;
        if (_ccButton != null) _ccButton.Content = Glyphs.Captions(on);
        _pop?.SetCaptions(on);
    }

    /// <summary>
    /// The CC button's menu: every subtitle track and, when a file has more
    /// than one, every audio track. Choosing one works exactly as the old
    /// drop-downs did, so the choice is still remembered by language.
    /// </summary>
    public void OpenTrackMenu(FrameworkElement anchor)
    {
        var list = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            VerticalOffset = -8,
            PopupAnimation = PopupAnimation.Fade,
        };
        void Section(string title, ComboBox box)
        {
            list.Children.Add(Ui.Caps(title, 10.2, Theme.Dim, 0.12, weight: FontWeights.ExtraBold).Margin(14, 8, 14, 6));
            foreach (var item in box.Items.OfType<ComboBoxItem>().ToList())
            {
                var on = ReferenceEquals(box.SelectedItem, item);
                var tick = Ui.Text(on ? "✓" : "", 13, Theme.Lamp, FontWeights.Bold);
                tick.Width = 20;
                var label = Ui.Text((string)item.Content, 13, on ? Theme.Lamp : Theme.Paper);
                var row = new DockPanel { LastChildFill = true };
                row.Children.Add(tick);
                row.Children.Add(label);
                var face = new Border { Padding = new Thickness(12, 6, 18, 6), Background = Theme.Clear, Child = row };
                var pick = item;
                var b = Ui.Bare(face, () =>
                {
                    popup.IsOpen = false;
                    box.SelectedItem = pick;
                });
                b.Focusable = false;
                b.MouseEnter += (_, _) => face.Background = Theme.Rail;
                b.MouseLeave += (_, _) => face.Background = Theme.Clear;
                list.Children.Add(b);
            }
        }
        Section("Subtitles", _subs);
        if (_audio.Items.Count > 1)
        {
            list.Children.Add(new Border { Height = 1, Background = Theme.Rail, Margin = new Thickness(0, 6, 0, 0) });
            Section("Audio", _audio);
        }
        popup.Child = new Border
        {
            Background = Theme.Shelf,
            BorderBrush = Theme.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(0, 2, 0, 6),
            MinWidth = 220,
            Child = new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        popup.Closed += (_, _) =>
        {
            MenuOpen = false;
            RestartHideTimer();
            _pop?.ShowChrome();
        };
        MenuOpen = true;
        _hideTimer.Stop();
        popup.IsOpen = true;
    }

    // ------------------------------------------------------------- pop-out
    PopOutWindow? _pop;
    bool _pauseWhenPlaying;

    // The subtitle and audio tracks showing when the picture moves between the
    // main window and the pop-out, put back once the file reopens there. Same
    // file, so the same track numbers — no guessing by language.
    (long FileId, int Spu, int Audio)? _carryTracks;

    void RememberTracks(long fileId)
    {
        if (_mp == null) return;
        _carryTracks = (fileId, _mp.Spu, _mp.AudioTrack);
    }

    string PopTitle(Row? item)
    {
        var isShow = _queue?.Kind == "show";
        return isShow && item != null ? $"{_title.Text} · {item.Str("label")}" : _title.Text;
    }

    /// <summary>
    /// Moves playback into the pop-out window and hands the main window back
    /// to the library. VLC cannot move a picture that is already showing to a
    /// new window, so the file is picked up again there from the same second.
    /// </summary>
    async Task PopOut()
    {
        if (_pop != null || _mp == null || _fileId == 0) return;
        if (_fullscreen) ToggleFullscreen();
        var mp = _mp;
        var fileId = _fileId;
        var at = Now();
        _pauseWhenPlaying = !mp.IsPlaying;
        RememberTracks(fileId);
        SaveProgress();

        var item = _index >= 0 && _queue != null ? _queue.Items[_index] : null;
        var pop = new PopOutWindow(this, PopTitle(item), _win.Icon);
        _pop = pop;
        pop.Show();
        await pop.Ready;
        await Task.Run(() => mp.Stop());
        DropView();
        pop.View.MediaPlayer = mp;
        // The pop-out's controls stay above everything, the same as the window.
        foreach (Window w in Application.Current.Windows)
            if (ReferenceEquals(w.Owner, pop)) w.Topmost = true;
        Visibility = Visibility.Collapsed;
        _win.UpdateBack();
        DrawMute();
        DrawCc();
        await PlayFile(fileId, at);
        foreach (Window w in Application.Current.Windows)
            if (ReferenceEquals(w.Owner, pop)) w.Topmost = true;
    }

    /// <summary>Back into the main window, carrying on from the same second.</summary>
    public async Task ReturnFromPopOut()
    {
        var pop = _pop;
        var mp = _mp;
        if (pop == null || mp == null) return;
        var fileId = _fileId;
        var at = Now();
        _pauseWhenPlaying = !mp.IsPlaying;
        RememberTracks(fileId);
        SaveProgress();
        await Task.Run(() => mp.Stop());
        ClosePop();
        Visibility = Visibility.Visible;
        _win.UpdateBack();
        if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Maximized;
        _win.Activate();
        await EnsureView();
        await PlayFile(fileId, at);
        Focus();
    }

    void ClosePop()
    {
        var pop = _pop;
        _pop = null;
        if (pop == null) return;
        try
        {
            pop.View.MediaPlayer = null;
            pop.View.Dispose();
        }
        catch (Exception ex) { App.Log("Pop-out surface: " + ex.Message); }
        pop.CloseFromPlayer();
    }

    public void PopToggleMute() => ToggleMute();

    public void PopSkip(int seconds)
    {
        if (_mp == null) return;
        SeekTo(Now() + seconds);
    }

    public void PopSeek(double seconds) => SeekTo(seconds);

    /// <summary>The same keys as the main player, less the ones about the main window.</summary>
    public void PopKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape or Key.F or Key.F11) return;
        HandleKey(e);
        _pop?.ShowChrome();
    }

    // ------------------------------------------------------------- keyboard
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.OriginalSource is TextBox || (e.OriginalSource is DependencyObject d && FindAncestor<ComboBox>(d) is { IsDropDownOpen: true }))
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                if (_fullscreen) ToggleFullscreen();
                else _ = CloseAsync();
                break;
            case Key.Space:
            case Key.K:
            case Key.MediaPlayPause:
                TogglePause();
                break;
            case Key.Left:
            case Key.J:
                SkipBy(-Skip);
                break;
            case Key.Right:
            case Key.L:
                SkipBy(Skip);
                break;
            case Key.Up:
                _volume.Value = Math.Min(_volume.Maximum, _volume.Value + 5);
                Flash($"Volume {(int)_volume.Value}");
                break;
            case Key.Down:
                _volume.Value = Math.Max(0, _volume.Value - 5);
                Flash($"Volume {(int)_volume.Value}");
                break;
            case Key.N:
            case Key.MediaNextTrack:
                _ = GoTo(1);
                break;
            case Key.P:
            case Key.MediaPreviousTrack:
                _ = GoTo(-1);
                break;
            case Key.C:
                Cycle(_subs);
                break;
            case Key.D:
                Cycle(_audio);
                break;
            case Key.M:
                ToggleMute();
                break;
            case Key.A:
                _autoNext.IsChecked = _autoNext.IsChecked != true;
                break;
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                break;
            default:
                return;
        }
        ShowControls();
        e.Handled = true;
    }

    static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T hit) return hit;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
