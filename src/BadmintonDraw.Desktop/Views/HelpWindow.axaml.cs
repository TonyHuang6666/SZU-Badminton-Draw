using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using BadmintonDraw.Desktop.Help;
using BadmintonDraw.Desktop.ViewModels;

namespace BadmintonDraw.Desktop.Views;

public partial class HelpWindow : Window
{
    private readonly HelpViewModel model = new();
    private bool selecting;

    public HelpWindow()
    {
        InitializeComponent();
        DataContext = model;
        HelpDocument.LinkClicked += model.Navigate;
        HelpDocument.CopyStatusChanged += message => model.StatusText = message;
        model.NavigationRequested += Navigate;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(HelpViewModel.SelectedDocument) or nameof(HelpViewModel.ReadingFontSize)) Render();
            if (args.PropertyName == nameof(HelpViewModel.SearchText)) Dispatcher.UIThread.Post(() => Find(false), DispatcherPriority.Loaded);
        };
        Render();
    }

    private void Render()
    {
        HelpDocument.SetDocument(model.SelectedDocument.Markdown, model.ReadingFontSize);
        selecting = true;
        HelpUserDocuments.SelectedItem = model.UserDocuments.Contains(model.SelectedDocument) ? model.SelectedDocument : null;
        HelpTechnicalDocuments.SelectedItem = model.TechnicalDocuments.Contains(model.SelectedDocument) ? model.SelectedDocument : null;
        selecting = false;
        if (model.HasSearchText) Dispatcher.UIThread.Post(() => Find(false), DispatcherPriority.Loaded);
    }

    private void SelectDocument(object? sender, SelectionChangedEventArgs e)
    {
        if (!selecting && e.AddedItems.OfType<HelpDocument>().FirstOrDefault() is { } page) model.SelectedDocument = page;
    }

    private async void Navigate(HelpLinkTarget target)
    {
        if (target.Kind == HelpLinkKind.Internal)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!HelpDocument.ScrollToAnchor(target.Anchor)) model.StatusText = "已打开文档，但未找到链接中的章节。";
            }, DispatcherPriority.Loaded);
        }
        else if (target.Kind == HelpLinkKind.External && target.ExternalUri is { } uri)
        {
            try
            {
                if (!await Launcher.LaunchUriAsync(uri)) model.StatusText = "未能打开浏览器，请检查系统默认浏览器。";
            }
            catch (Exception) { model.StatusText = "未能打开浏览器，请检查系统默认浏览器。"; }
        }
    }

    private void DecreaseFont(object? sender, RoutedEventArgs e) => model.ReadingFontSize -= 1;
    private void IncreaseFont(object? sender, RoutedEventArgs e) => model.ReadingFontSize += 1;
    private void FindNext(object? sender, RoutedEventArgs e) => Find(true);
    private void Find(bool next)
    {
        var result = HelpDocument.FindText(model.SearchText, next);
        model.StatusText = model.HasSearchText
            ? result.Count > 0 ? $"当前文档：第 {result.Current} / {result.Count} 处匹配。点击“下一处”继续查找。" : "当前文档未找到匹配文字，请从目录选择搜索结果。"
            : "内置文档可离线阅读。正文可选择并复制。";
    }
    private void ToggleTheme(object? sender, RoutedEventArgs e) => RequestedThemeVariant =
        ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;

    private async void CopyText(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } clipboard) { model.StatusText = "系统剪贴板暂时不可用，请选择正文复制。"; return; }
            await clipboard.SetTextAsync(model.SelectedDocument.PlainText);
            model.StatusText = "已复制当前文档的全文。";
        }
        catch (Exception) { model.StatusText = "复制失败，请选择正文后复制。"; }
    }
}
