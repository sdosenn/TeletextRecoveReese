using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace TeletextRecoveReese;

public class App : Application
{
    public static int UiScalePercent { get; set; } = 100;
    public static string? GridFontFamilyName { get; set; }
    public static FontFamily? GridFontFamily { get; set; }
    public static bool TerminatorAboutUnlocked { get; set; }

    public static void ApplyUiScale(Window window)
    {
        if (window.Classes.Contains("application-ui-scaled")) return;
        window.Classes.Add("application-ui-scaled");

        double scale = Math.Clamp(UiScalePercent, 100, 200) / 100.0;
        if (Math.Abs(scale - 1.0) < 0.001 || window.Content is not Control content)
            return;

        window.Content = null;
        var scaledContent = new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(scale, scale),
            Child = content,
        };
        window.Content = scaledContent;
        if (!double.IsNaN(window.Width) && window.Width > 0)
            window.Width *= scale;
        if (!double.IsNaN(window.Height) && window.Height > 0)
            window.Height *= scale;
        if (window.MinWidth > 0)
            window.MinWidth *= scale;
        if (window.MinHeight > 0)
            window.MinHeight *= scale;
    }

    public override void Initialize()
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (window is not MainWindow)
                ApplyUiScale(window);
        });
        AvaloniaXamlLoader.Load(this);
        if (!OperatingSystem.IsMacOS())
            ClearValue(NativeMenu.MenuProperty);
    }

    private void OnNativeAboutClicked(object? sender, EventArgs e)
    {
        var aboutWindow = new AboutWindow();
        ApplyUiScale(aboutWindow);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            aboutWindow.ShowDialog(owner);
        else
            aboutWindow.Show();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool startNew = desktop.Args?.Any(argument =>
                string.Equals(argument, "-startnew", StringComparison.OrdinalIgnoreCase)) == true;
            bool loadLast = !startNew && desktop.Args?.Any(argument =>
                string.Equals(argument, "-loadlast", StringComparison.OrdinalIgnoreCase)) == true;
            desktop.MainWindow = new MainWindow(loadLast, startNew);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
