namespace BadmintonDraw.Desktop.ViewModels;

public sealed class UnavailableCourtSelectionViewModel : ViewModelBase
{
    private readonly IReadOnlyList<string> _unknownReferences;
    private bool _allCourts;
    private bool _correctedUnknownReferences;

    public UnavailableCourtSelectionViewModel(IReadOnlyList<string> availableCourts, IReadOnlyList<string> selectedCourts)
    {
        var available = availableCourts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = selectedCourts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _unknownReferences = Array.AsReadOnly(selectedCourts.Where(court => !available.Contains(court, StringComparer.OrdinalIgnoreCase)).ToArray());
        _allCourts = selectedCourts.Count == 0;
        Courts = Array.AsReadOnly(available.Select(name =>
        {
            var delimiter = name.IndexOf(" · ", StringComparison.Ordinal);
            var label = delimiter >= 0 ? name[(delimiter + 3)..] : name;
            return new CourtChoiceViewModel(name, label, selected.Contains(name), CourtSelectionChanged);
        }).ToArray());
    }

    public IReadOnlyList<CourtChoiceViewModel> Courts { get; }
    public bool AllCourts
    {
        get => _allCourts;
        set
        {
            if (!SetProperty(ref _allCourts, value)) return;
            if (value) _correctedUnknownReferences = true;
            NotifySelectionProperties();
        }
    }
    public bool CanAccept => ValidationMessage.Length == 0;
    public string ValidationMessage
    {
        get
        {
            if (AllCourts) return "";
            if (!_correctedUnknownReferences && _unknownReferences.Count > 0)
                return $"原时段引用的场地已不在当日场地中：{string.Join("、", _unknownReferences)}。请重新勾选场地或选择全部场地。";
            if (!Courts.Any(court => court.IsSelected)) return "请选择至少一片场地，或明确选择全部场地。";
            return "";
        }
    }
    public string SelectionSummary => AllCourts ? "当日全部场地不可用" : $"已选 {Courts.Count(court => court.IsSelected)} 片不可用场地";

    public bool TryCreateSelection(out IReadOnlyList<string> courts)
    {
        courts = [];
        if (!CanAccept) return false;
        if (!AllCourts) courts = Array.AsReadOnly(Courts.Where(court => court.IsSelected).Select(court => court.Name).ToArray());
        return true;
    }

    private void CourtSelectionChanged()
    {
        _correctedUnknownReferences = true;
        if (_allCourts)
        {
            _allCourts = false;
            OnPropertyChanged(nameof(AllCourts));
        }
        NotifySelectionProperties();
    }

    private void NotifySelectionProperties()
    {
        OnPropertyChanged(nameof(CanAccept));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(SelectionSummary));
    }
}
