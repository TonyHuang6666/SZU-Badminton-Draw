using BadmintonDraw.Core.Scheduling;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed record CourtSelectionConstraint(string Label, IReadOnlyList<string> Courts);

public sealed class CourtChoiceViewModel(string name, string label, bool isSelected = false, Action? changed = null) : ViewModelBase
{
    private bool _isSelected = isSelected;
    public string Name { get; } = name;
    public string Label { get; } = label;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value)) changed?.Invoke(); }
    }
}

public sealed class VenueCourtGroupViewModel(string name, IReadOnlyList<CourtChoiceViewModel> courts)
{
    public string Name { get; } = name;
    public IReadOnlyList<CourtChoiceViewModel> Courts { get; } = Array.AsReadOnly(courts.ToArray());
    public DelegateCommand SelectAllCommand { get; } = new(() => { foreach (var court in courts) court.IsSelected = true; });
    public DelegateCommand ClearCommand { get; } = new(() => { foreach (var court in courts) court.IsSelected = false; });
}

public sealed class VenueCourtSelectionViewModel : ViewModelBase
{
    private static readonly char[] Separators = [',', '，', ';', '；', '\n', '\r'];
    private readonly IReadOnlyList<CourtSelectionConstraint> _constraints;
    private readonly IReadOnlyList<VenueCourtGroupViewModel>[] _presetDrafts;
    private readonly IReadOnlyList<string>? _originalCustomCourts;
    private IReadOnlyList<string>? _originalCustomLabels;
    private bool _customDraftEdited;
    private int _venueIndex = -1;
    private string _customVenueName = "";
    private string _customCourtsText = "";
    private string _numberedCourtCountText = "8";
    private string _generationError = "";
    private bool _confirmAdjustUnavailable;

    public VenueCourtSelectionViewModel(IReadOnlyList<string> existingCourts, IReadOnlyList<CourtSelectionConstraint>? constraints = null)
    {
        var original = existingCourts.ToArray();
        _constraints = Array.AsReadOnly((constraints ?? []).Select(constraint =>
            new CourtSelectionConstraint(constraint.Label, Array.AsReadOnly(constraint.Courts.ToArray()))).ToArray());
        Venues = Array.AsReadOnly(BuiltInVenueCatalog.Venues.Select(venue => venue.DisplayName).Append("自定义场馆").ToArray());
        _presetDrafts = BuiltInVenueCatalog.Venues.Select(venue =>
            (IReadOnlyList<VenueCourtGroupViewModel>)Array.AsReadOnly(venue.Groups.Select(group =>
                new VenueCourtGroupViewModel(group.Name, group.Courts.Select(label =>
                    new CourtChoiceViewModel(venue.QualifiedCourtName(label), label, false, SelectionChanged)).ToArray())).ToArray())).ToArray();
        GenerateNumberedCourtsCommand = new(GenerateNumberedCourts);
        SelectAllCommand = new(() => SetAll(true), () => IsPreset);
        ClearCommand = new(() => SetAll(false), () => IsPreset);

        if (original.Length == 0) return;
        var preset = original.Distinct(StringComparer.OrdinalIgnoreCase).Count() == original.Length
            ? BuiltInVenueCatalog.FindForCourts(original) : null;
        if (preset is not null)
        {
            _venueIndex = BuiltInVenueCatalog.Venues.ToList().IndexOf(preset);
            var selected = original.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var court in Groups.SelectMany(group => group.Courts)) court.IsSelected = selected.Contains(court.Name);
        }
        else
        {
            _venueIndex = BuiltInVenueCatalog.Venues.Count;
            _originalCustomCourts = Array.AsReadOnly(original);
            RestoreCustomDraft(original);
        }
    }

    public IReadOnlyList<string> Venues { get; }
    public int VenueIndex
    {
        get => _venueIndex;
        set
        {
            var next = value >= -1 && value < Venues.Count ? value : -1;
            if (!SetProperty(ref _venueIndex, next)) return;
            _generationError = "";
            OnPropertyChanged(nameof(IsPreset));
            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(Groups));
            SelectAllCommand.NotifyCanExecuteChanged();
            ClearCommand.NotifyCanExecuteChanged();
            SelectionChanged();
        }
    }
    public bool IsPreset => VenueIndex >= 0 && VenueIndex < BuiltInVenueCatalog.Venues.Count;
    public bool IsCustom => VenueIndex == BuiltInVenueCatalog.Venues.Count;
    public IReadOnlyList<VenueCourtGroupViewModel> Groups => IsPreset ? _presetDrafts[VenueIndex] : [];
    public string CustomVenueName
    {
        get => _customVenueName;
        set
        {
            if (!SetProperty(ref _customVenueName, value ?? "")) return;
            _customDraftEdited = true;
            SelectionChanged();
        }
    }
    public string CustomCourtsText
    {
        get => _customCourtsText;
        set
        {
            if (!SetProperty(ref _customCourtsText, value ?? "")) return;
            _customDraftEdited = true;
            _originalCustomLabels = null;
            _generationError = "";
            OnPropertyChanged(nameof(GenerateNumberedCourtsLabel));
            SelectionChanged();
        }
    }
    public string NumberedCourtCountText
    {
        get => _numberedCourtCountText;
        set
        {
            if (!SetProperty(ref _numberedCourtCountText, value ?? "")) return;
            _generationError = "";
            NotifySelectionProperties();
        }
    }
    public DelegateCommand GenerateNumberedCourtsCommand { get; }
    public string GenerateNumberedCourtsLabel => string.IsNullOrWhiteSpace(CustomCourtsText) ? "生成编号场地" : "替换为编号场地";
    public DelegateCommand SelectAllCommand { get; }
    public DelegateCommand ClearCommand { get; }
    public string SelectionSummary
    {
        get
        {
            var courts = CreateDraft(out _);
            if (IsPreset) return $"已选 {courts.Count} / {Groups.Sum(group => group.Courts.Count)} 片";
            return IsCustom ? $"共 {courts.Count} 片场地" : "尚未选择场地";
        }
    }
    public string ValidationMessage
    {
        get
        {
            if (_generationError.Length > 0) return _generationError;
            CreateDraft(out var error);
            if (error.Length > 0) return error;
            return HasAffectedWindows && !ConfirmAdjustUnavailable ? "请确认调整受影响的不可用时段后再应用。" : "";
        }
    }
    public bool CanAccept => ValidationMessage.Length == 0;
    public bool HasAffectedWindows => AffectedWindows().Count > 0;
    public string AffectedWindowsMessage
    {
        get
        {
            var messages = AffectedWindows().Select(affected =>
                $"「{affected.Constraint.Label}」受影响场地：{string.Join("、", affected.Removed)}。" +
                (affected.Removed.Count == affected.Constraint.Courts.Count
                    ? "确认后删除该不可用时段。" : "确认后仅保留仍被选中的场地。"));
            var allCourts = _constraints.Where(constraint => constraint.Courts.Count == 0).Select(constraint =>
                $"「{constraint.Label}」为全场不可用，仍适用于新选场地。");
            return string.Join(Environment.NewLine, messages.Concat(allCourts));
        }
    }
    public bool ConfirmAdjustUnavailable
    {
        get => _confirmAdjustUnavailable;
        set { if (SetProperty(ref _confirmAdjustUnavailable, value)) NotifySelectionProperties(); }
    }

    public bool TryCreateSelection(out IReadOnlyList<string> courts)
    {
        courts = [];
        if (!CanAccept) return false;
        courts = Array.AsReadOnly(CreateDraft(out _).ToArray());
        return true;
    }

    private void RestoreCustomDraft(string[] courts)
    {
        const string separator = " · ";
        var firstSeparator = courts[0].IndexOf(separator, StringComparison.Ordinal);
        var prefix = firstSeparator > 0 ? courts[0][..firstSeparator] : "";
        var qualifiedPrefix = prefix + separator;
        var commonPrefix = prefix.Length > 0 && courts.All(court => court.StartsWith(qualifiedPrefix, StringComparison.Ordinal));
        _customVenueName = commonPrefix ? prefix : "";
        var labels = commonPrefix ? courts.Select(court => court[qualifiedPrefix.Length..]).ToArray() : courts;
        _originalCustomLabels = Array.AsReadOnly(labels.ToArray());
        _customCourtsText = string.Join(Environment.NewLine, labels);
    }

    private IReadOnlyList<string> CreateDraft(out string error)
    {
        error = "";
        if (!IsPreset && !IsCustom) { error = "请选择场馆。"; return []; }
        IReadOnlyList<string> courts;
        if (IsPreset)
        {
            courts = Groups.SelectMany(group => group.Courts).Where(court => court.IsSelected).Select(court => court.Name).ToArray();
        }
        else
        {
            if (CustomVenueName.IndexOfAny(Separators) >= 0) error = "场馆名称不能包含逗号、分号或换行。";
            var labels = _originalCustomLabels ?? CustomCourtsText.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (labels.Any(string.IsNullOrWhiteSpace)) error = "请填写场地名称，场地列表不能包含空项。";
            var venueName = CustomVenueName.Trim();
            courts = !_customDraftEdited && _originalCustomCourts is not null
                ? _originalCustomCourts
                : labels.Select(label => venueName.Length == 0 ? label : $"{venueName} · {label}").ToArray();
        }
        if (courts.Count == 0) error = "请选择至少一片场地。";
        if (courts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != courts.Count) error = "场地名称不能重复（不区分大小写）。";
        return courts;
    }

    private IReadOnlyList<(CourtSelectionConstraint Constraint, IReadOnlyList<string> Removed)> AffectedWindows()
    {
        var selected = CreateDraft(out _).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _constraints.Where(constraint => constraint.Courts.Count > 0)
            .Select(constraint => (Constraint: constraint, Removed: (IReadOnlyList<string>)constraint.Courts.Where(court => !selected.Contains(court)).ToArray()))
            .Where(affected => affected.Removed.Count > 0).ToArray();
    }

    private void SetAll(bool selected)
    {
        foreach (var court in Groups.SelectMany(group => group.Courts)) court.IsSelected = selected;
    }

    private void GenerateNumberedCourts()
    {
        if (!IsCustom) return;
        if (!int.TryParse(NumberedCourtCountText, out var count) || count is < 1 or > 256)
        {
            _generationError = "编号场地数量请输入 1 至 256 的整数。";
            NotifySelectionProperties();
            return;
        }
        CustomCourtsText = string.Join(Environment.NewLine, Enumerable.Range(1, count).Select(number => $"{number}号场"));
    }

    private void SelectionChanged()
    {
        if (_confirmAdjustUnavailable)
        {
            _confirmAdjustUnavailable = false;
            OnPropertyChanged(nameof(ConfirmAdjustUnavailable));
        }
        NotifySelectionProperties();
    }

    private void NotifySelectionProperties()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(CanAccept));
        OnPropertyChanged(nameof(HasAffectedWindows));
        OnPropertyChanged(nameof(AffectedWindowsMessage));
    }
}
