using System;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Input;

namespace TeletextRecoveReese;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Title = $"About {AppVersion.ProductName}";
        VersionText.Text = $"Version {AppVersion.DisplayVersion}";
    }

    /// <summary>
    /// Handles the close button click event to dismiss the About window.
    /// </summary>
    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnSupportLinkPressed(object? sender, PointerPressedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://ko-fi.com/sinisinavideoteka"));
        e.Handled = true;
    }

    private async void OnRepositoryLinkPressed(object? sender, PointerPressedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://github.com/sdosenn/TeletextRecoveReese"));
        e.Handled = true;
    }
}
