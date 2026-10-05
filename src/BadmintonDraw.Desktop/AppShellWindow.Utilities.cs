using Avalonia.Controls;
using Avalonia.Interactivity;
using BadmintonDraw.Desktop.Views;

namespace BadmintonDraw.Desktop;

public partial class AppShellWindow
{
    private HelpWindow? helpWindow;
    private TemplateExportWindow? templateWindow;

    private void ConfigureUtilityWindows()
    {
        Closed += (_, _) =>
        {
            helpWindow?.Close();
            templateWindow?.Close();
        };
    }

    private void HelpClicked(object? sender, RoutedEventArgs args)
    {
        if (helpWindow is { } current) { ActivateUtility(current); return; }
        var window = new HelpWindow { Icon = Icon };
        helpWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(helpWindow, window)) helpWindow = null; };
        window.Show(this);
    }

    private void TemplatesClicked(object? sender, RoutedEventArgs args)
    {
        if (templateWindow is { } current) { ActivateUtility(current); return; }
        var window = new TemplateExportWindow { Icon = Icon };
        templateWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(templateWindow, window)) templateWindow = null; };
        window.Show(this);
    }

    private static void ActivateUtility(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
