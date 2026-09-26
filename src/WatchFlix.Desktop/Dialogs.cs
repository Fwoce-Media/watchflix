using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WatchFlix.Core;

namespace WatchFlix.Desktop;

/// <summary>Dark child windows for logs, confirmations and editing.</summary>
public static class Dialogs
{
    static readonly List<Window> Open = new();

    public static Window Create(Window owner, string title, double width, double height)
    {
        var w = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            Height = height,
            Background = Theme.Shelf,
            BorderBrush = Theme.Lamp,
            BorderThickness = new Thickness(0, 3, 0, 0),
            Foreground = Theme.Paper,
            FontFamily = Theme.Sans,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResizeWithGrip,
        };
        App.DarkTitleBar(w);
        w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { w.Close(); e.Handled = true; }
        };
        w.Closed += (_, _) => Open.Remove(w);
        Open.Add(w);
        return w;
    }

    public static Window Show(Window owner, string title, UIElement content, double width = 640, double height = 480)
    {
        var w = Create(owner, title, width, height);
        w.Content = new Border { Padding = new Thickness(20), Child = content };
        w.Show();
        return w;
    }

    public static bool CloseTop()
    {
        var top = Open.LastOrDefault();
        if (top == null) return false;
        top.Close();
        return true;
    }

    /// <summary>A question with two answers. Returns true for the first.</summary>
    public static bool Confirm(Window owner, string title, UIElement body, string yes, string no = "Cancel",
                               double width = 560, double height = 420)
    {
        var w = Create(owner, title, width, height);
        var result = false;
        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroller = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        grid.Children.Add(scroller);
        var yesButton = Ui.Button(yes, () => { result = true; w.Close(); }, primary: true);
        var noButton = Ui.Button(no, () => w.Close());
        noButton.Margin = new Thickness(10, 0, 0, 0);
        var buttons = Ui.Row(yesButton, noButton);
        buttons.Margin = new Thickness(0, 16, 0, 0);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(buttons, 1);
        grid.Children.Add(buttons);
        w.Content = grid;
        w.ShowDialog();
        return result;
    }

    public static void Message(Window owner, string title, string text)
    {
        var body = Ui.Text(text, 14, Theme.Paper, wrap: true);
        var w = Create(owner, title, 520, 260);
        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var ok = Ui.Button("OK", () => w.Close(), primary: true);
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(ok, 1);
        grid.Children.Add(ok);
        w.Content = grid;
        w.ShowDialog();
    }
}
