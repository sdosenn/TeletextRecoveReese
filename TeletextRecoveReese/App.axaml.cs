using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace TeletextRecoveReese;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (!OperatingSystem.IsMacOS())
            ClearValue(NativeMenu.MenuProperty);
    }

    private void OnNativeAboutClicked(object? sender, EventArgs e)
    {
        var aboutWindow = new AboutWindow();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            aboutWindow.ShowDialog(owner);
        else
            aboutWindow.Show();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool loadLast = desktop.Args?.Any(argument =>
                string.Equals(argument, "-loadlast", StringComparison.OrdinalIgnoreCase)) == true;
            desktop.MainWindow = new MainWindow(loadLast);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
