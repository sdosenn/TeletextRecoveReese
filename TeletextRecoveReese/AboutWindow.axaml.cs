using System;
using Avalonia.Interactivity;
using Avalonia.Controls;

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

    private async void OnSupportLinkClicked(object? sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://ko-fi.com/sinisinavideoteka"));
    }

    private async void OnRepositoryLinkClicked(object? sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://github.com/sdosenn/TeletextRecoveReese"));
    }
}
