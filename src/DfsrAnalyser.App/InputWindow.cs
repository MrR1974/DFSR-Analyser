using System.Windows;
using System.Windows.Controls;

namespace DfsrAnalyser.App;

/// <summary>A one-line text prompt (WPF has no built-in input box).</summary>
public sealed class InputWindow : Window
{
    private readonly TextBox _box;

    private InputWindow(string title, string prompt, string initial)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 3, 16, 3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        Content = new StackPanel { Margin = new Thickness(14), Children = { new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap }, _box, buttons } };
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var w = new InputWindow(title, prompt, initial) { Owner = owner };
        return w.ShowDialog() == true && !string.IsNullOrWhiteSpace(w._box.Text) ? w._box.Text.Trim() : null;
    }
}
