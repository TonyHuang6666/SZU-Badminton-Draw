using BadmintonDraw.Desktop.Help;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class HelpViewModel : ViewModelBase
{
    private readonly HelpCatalog catalog = new();
    private string searchText = "";
    private HelpDocument selectedDocument;
    private double readingFontSize = 15;
    private string statusText = "内置文档可离线阅读。正文可选择并复制。";

    public HelpViewModel() => selectedDocument = catalog.Documents.Single(d => d.Path == "README.md");
    public IReadOnlyList<HelpDocument> IntroDocuments => Search("程序介绍");
    public IReadOnlyList<HelpDocument> UserDocuments => Search("使用帮助");
    public IReadOnlyList<HelpDocument> TechnicalDocuments => Search("技术参考");
    public bool HasNoSearchResults => IntroDocuments.Count + UserDocuments.Count + TechnicalDocuments.Count == 0;
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value ?? "")) return;
            OnPropertyChanged(nameof(IntroDocuments));
            OnPropertyChanged(nameof(UserDocuments)); OnPropertyChanged(nameof(TechnicalDocuments));
            OnPropertyChanged(nameof(HasNoSearchResults));
            OnPropertyChanged(nameof(HasSearchText));
        }
    }
    public HelpDocument SelectedDocument
    {
        get => selectedDocument;
        set { if (value is not null) SetProperty(ref selectedDocument, value); }
    }
    public double ReadingFontSize
    {
        get => readingFontSize;
        set => SetProperty(ref readingFontSize, Math.Clamp(value, 12, 24));
    }
    public string StatusText { get => statusText; set => SetProperty(ref statusText, value); }
    public event Action<HelpLinkTarget>? NavigationRequested;

    public void Navigate(string href)
    {
        var target = catalog.ResolveLink(SelectedDocument.Path, href);
        switch (target.Kind)
        {
            case HelpLinkKind.Internal:
                SelectedDocument = catalog.Documents.Single(d => d.Path == target.DocumentPath);
                StatusText = "内置文档可离线阅读。正文可选择并复制。";
                break;
            case HelpLinkKind.Unavailable:
                StatusText = "此链接指向未内置的源码、样例或归档资料；请到项目 GitHub 仓库查阅。";
                break;
            case HelpLinkKind.Blocked:
                StatusText = "此链接类型不受支持，未打开。";
                break;
        }
        NavigationRequested?.Invoke(target);
    }

    private IReadOnlyList<HelpDocument> Search(string category)
    {
        var query = SearchText.Trim();
        return catalog.Documents.Where(d => d.Category == category &&
            (query.Length == 0 || d.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             d.Markdown.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
}
