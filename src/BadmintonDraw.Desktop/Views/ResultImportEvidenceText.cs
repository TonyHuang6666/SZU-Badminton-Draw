using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Media;
using BadmintonDraw.Desktop.ViewModels;

namespace BadmintonDraw.Desktop.Views;

/// <summary>Keep blank paragraphs out of Avalonia 12's wrapped multiline formatter, without changing the evidence.</summary>
public sealed class ResultImportEvidenceText : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<ResultImportEvidenceText, string?>(nameof(Text));
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public AsyncCommand CopyAllCommand { get; }

    public ResultImportEvidenceText()
    {
        ItemTemplate = new FuncDataTemplate<string>((line, _) => new SelectableTextBlock
        {
            // The full row, including whitespace, is a selection/context-menu hit target.
            Text = line, TextWrapping = TextWrapping.Wrap, ContextMenu = CreateCopyMenu(), Background = Brushes.Transparent,
            // Empty source lines retain a line of space, but contain no fabricated text.
            [!MinHeightProperty] = new Avalonia.Data.Binding(nameof(SelectableTextBlock.FontSize))
            { RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.Self) }
        });
        CopyAllCommand = new(async () =>
        {
            var original = Text;
            if (original is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(original);
        }, () => Text is not null && TopLevel.GetTopLevel(this)?.Clipboard is not null);
        ContextMenu = CreateCopyMenu();
        ToolTip.SetTip(this, "可选择单行文本；右键选择“复制完整文本”可复制全部原文。");
        AttachedToVisualTree += (_, _) => CopyAllCommand.NotifyCanExecuteChanged();
    }

    // Keep the full-text action available on each selectable line as well as the enclosing evidence.
    private ContextMenu CreateCopyMenu() => new() { Items = { new MenuItem { Header = "复制完整文本", Command = CopyAllCommand } } };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            // Only presentation line separators are normalized; Text and clipboard retain the exact original bytes-as-text.
            ItemsSource = Text?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n') ?? Array.Empty<string>();
            CopyAllCommand.NotifyCanExecuteChanged();
        }
    }
}
