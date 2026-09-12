using Avalonia.Controls;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Scheduling;
using BadmintonDraw.Desktop.ViewModels;

namespace BadmintonDraw.Desktop.Views;

public partial class ScheduleBoardPage : UserControl
{
    private ScheduleBoardPageViewModel? page;
    public ScheduleBoardPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindPage();
        AttachedToVisualTree += async (_, _) => { BindPage(); if (page is { } current) await current.InitializeAsync(); };
        DetachedFromVisualTree += (_, _) => UnbindPage();
        ScheduleBoard.MatchSelected += key => page?.SelectMatch(key);
        ScheduleBoard.ManualMoveRequested += key => { page?.SelectMatch(key); MoveEditor.IsExpanded = true; TargetTime.Focus(); };
        ScheduleBoard.MoveRequested += MoveRequested;
    }
    private void BindPage()
    {
        UnbindPage(); page = DataContext as ScheduleBoardPageViewModel;
        if (page is not { } current) return;
        current.FocusRequested += FocusMatch; ScheduleBoard.PreviewHoverAsync = current.PreviewHoverAsync;
    }
    private void UnbindPage()
    {
        if (page is { } old) old.FocusRequested -= FocusMatch;
        ScheduleBoard.PreviewHoverAsync = null; page = null;
    }
    private void FocusMatch(WorkspaceMatchKey key) => ScheduleBoard.FocusMatch(key);
    private async void MoveRequested(WorkspaceBoardMoveIntent intent)
    {
        if (page is not { } current) return;
        MoveEditor.IsExpanded = true;
        try { await current.RequestMoveAsync(intent); }
        catch (Exception exception) { current.ReportError(exception); }
    }
}
