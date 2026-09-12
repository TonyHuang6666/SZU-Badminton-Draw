using System.Diagnostics;
using System.Security.Cryptography;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using BadmintonDraw.Persistence;

namespace BadmintonDraw.Workflows.Tournaments;

/// <summary>Explicit synchronous commands. Desktop callers run slow commands off the UI thread.</summary>
public sealed partial class TournamentWorkspaceWorkflow(ITournamentWorkspaceStore? store = null, DrawPackageWorkflow? drawPackages = null)
{
    private readonly ITournamentWorkspaceStore store = store ?? new TournamentWorkspaceStore();
    private readonly DrawPackageWorkflow drawPackages = drawPackages ?? new();
    private readonly object sessionGate = new();
    private WorkspaceSession? currentSession;
    private bool notifyingSession;
    public WorkspaceSession? CurrentSession => Volatile.Read(ref currentSession);

    /// <summary>Raised on the command thread after publication; UI subscribers marshal to their dispatcher.</summary>
    public event EventHandler<WorkspaceSession>? SessionChanged;

    public WorkspaceCommandResult CreateWorkspace(CreateWorkspaceRequest request)
    {
        lock (sessionGate)
        {
            string? path = null;
            TournamentWorkspace? workspace = null;
            try
            {
                RequireOutsideNotification();
                path = Path.GetFullPath(request.WorkspacePath);
                Require(request.Projects.All(p => p.ProjectId is null), "project.new-id", "新建项目不能指定已有项目标识。");
                var projects = request.Projects.Select((p, i) => NewProject(p, i)).ToArray();
                workspace = TournamentWorkspace.Create(request.Name.Trim(), request.Kind, request.Purpose, projects);
                workspace = Audit(workspace, "WorkspaceCreated");
                var created = store.Create(path, workspace);
                InvalidateScheduleEditingSession();
                return Publish(created, path, null);
            }
            catch (Exception exception)
            {
                if (exception is WorkspaceStoreException { Committed: true } && workspace is not null && path is not null)
                    RefreshAfterCommit(new(workspace, path));
                throw WorkspaceCommandException.From(exception);
            }
        }
    }

    public WorkspaceCommandResult OpenWorkspace(string path)
    {
        lock (sessionGate)
        {
            try
            {
                RequireOutsideNotification();
                var fullPath = Path.GetFullPath(path);
                var opened = store.Read(fullPath);
                InvalidateScheduleEditingSession();
                return Publish(opened, fullPath, null);
            }
            catch (Exception exception) { throw WorkspaceCommandException.From(exception); }
        }
    }

    public WorkspaceCommandResult ImportRoster(Guid projectId, string inputPath, long expectedRevision) =>
        Change(expectedRevision, workspace =>
        {
            var project = EditableProject(workspace, projectId);
            var roster = ReadRoster(inputPath, project.Discipline);
            var projects = workspace.Projects.Select(p => p.Id == projectId ? p with { Roster = roster, Draw = null } : p).ToArray();
            return Audit(workspace with { Projects = projects,
                Stage = projects.All(p => p.Roster is not null) ? TournamentStage.RostersReady : TournamentStage.Draft },
                "RosterImported", projectId, roster.SourceFileName);
        });

    public WorkspaceCommandResult PreviewDraw(Guid projectId, DrawSettings settings, long expectedRevision) =>
        Change(expectedRevision, workspace =>
        {
            Require(workspace.Stage >= TournamentStage.RostersReady, "stage.rosters", "全部项目名单通过校验后才能开始抽签。");
            var project = EditableProject(workspace, projectId);
            Require(settings.CompetitionMode == project.CompetitionMode && settings.EventKind == EventKindFor(project.Discipline),
                "draw.settings", "抽签设置与项目不一致。");
            var draw = new DrawService().Generate(project.Roster!.Participants, settings);
            return Audit(ReplaceProject(workspace, project with { Draw = new(draw, null) }),
                "DrawPreviewed", projectId, "随机种子：" + draw.Audit.RandomSeed);
        });

    public WorkspaceCommandResult ConfirmDraw(Guid projectId, long expectedRevision) =>
        Change(expectedRevision, workspace =>
        {
            var project = EditableProject(workspace, projectId);
            Require(project.Draw is not null, "draw.preview-required", "请先生成并检查抽签预览。");
            var graph = MatchGraphFactory.Create(projectId, project.Draw!.Result);
            var updated = ReplaceProject(workspace, project with
                { Draw = project.Draw with { ConfirmedAt = DateTimeOffset.UtcNow }, MatchGraph = graph });
            if (updated.Projects.All(p => p.Draw?.ConfirmedAt is not null))
                updated = updated with { Stage = TournamentStage.DrawsConfirmed };
            return Audit(updated, "DrawConfirmed", projectId, graph.Revision);
        });

    public WorkspaceCommandResult ReopenDraw(Guid projectId, string reason, long expectedRevision) =>
        Change(expectedRevision, workspace => TournamentWorkspaceRules.ReopenDraw(workspace, projectId, reason));

    public WorkspaceCommandResult UpgradeToFullTournament(long expectedRevision) =>
        Change(expectedRevision, TournamentWorkspaceRules.UpgradeToFullTournament);

    public WorkspaceCommandResult UpdateConfiguration(UpdateWorkspaceConfigurationRequest request, long expectedRevision) =>
        Change(expectedRevision, workspace =>
        {
            Require(workspace.Projects.All(p => p.Draw?.ConfirmedAt is null) && workspace.Results.Count == 0,
                "configuration.frozen", "已有确认抽签，不能修改项目配置；请先解除抽签确认。");
            var projects = request.Projects.Select((item, i) =>
            {
                if (item.ProjectId is null) return NewProject(item, i);
                var previous = Project(workspace, item.ProjectId.Value);
                Require(previous.Discipline == item.Discipline, "project.discipline", "已有项目不能更换项目种类，请删除后新增项目。");
                return previous with { DisplayName = item.DisplayName?.Trim() ?? previous.DisplayName,
                    CompetitionMode = item.CompetitionMode, SortOrder = i,
                    Draw = previous.CompetitionMode == item.CompetitionMode ? previous.Draw : null };
            }).ToArray();
            var ready = projects.All(p => p.Roster is not null);
            if (!ready) projects = projects.Select(p => p with { Draw = null }).ToArray();
            return Audit(workspace with { Name = request.Name.Trim(), Projects = projects,
                Stage = ready ? TournamentStage.RostersReady : TournamentStage.Draft }, "ConfigurationUpdated");
        });

    private WorkspaceCommandResult Change(long expectedRevision, Func<TournamentWorkspace, TournamentWorkspace> mutation) =>
        WithCapturedSession(captured => CommitChange(captured, expectedRevision, mutation));

    // Search and publication can share this boundary without opening a second command or releasing the session gate.
    private T WithCapturedSession<T>(Func<WorkspaceSession, T> command)
    {
        // Capture before waiting: a queued command must never migrate to a newly opened archive with the same revision.
        var captured = CurrentSession;
        lock (sessionGate)
        {
            try
            {
                RequireOutsideNotification();
                Require(captured is not null, "workspace.not-open", "请先新建或打开赛事工作区。");
                Require(ReferenceEquals(captured, currentSession), "workspace.session-changed", "当前工作区已切换，请在新工作区重新执行操作。");
                Require(!captured!.RequiresReload, "workspace.reload-required", "工作区已保存但无法重新读取，请重新打开后再操作。");
                return command(captured);
            }
            catch (Exception exception)
            {
                if (exception is WorkspaceStoreException { Committed: true } && captured is not null)
                    RefreshAfterCommit(captured);
                throw WorkspaceCommandException.From(exception);
            }
        }
    }

    private WorkspaceCommandResult CommitChange(WorkspaceSession captured, long expectedRevision,
        Func<TournamentWorkspace, TournamentWorkspace> mutation, Action? afterCommit = null)
    {
        var result = store.Mutate(captured.WorkspacePath, expectedRevision, workspace =>
        {
            RequireWorkspaceIdentity(workspace, captured);
            var candidate = mutation(workspace);
            TournamentWorkspaceRules.Validate(candidate);
            return candidate;
        });
        // Ephemeral undo state must be visible to observers of the saved snapshot. It cannot undo
        // durable publication or turn a committed save into a reported failure if a callback fails.
        try { afterCommit?.Invoke(); }
        catch (Exception exception)
        {
            undoScheduleEdits = [];
            try { Trace.TraceError("赛程撤销状态刷新失败：{0}", exception); } catch { /* Best effort. */ }
        }
        return Publish(result.Workspace, captured.WorkspacePath, result.BackupPath);
    }

    private static void RequireWorkspaceIdentity(TournamentWorkspace workspace, WorkspaceSession captured) =>
        Require(workspace.Id == captured.Workspace.Id, "workspace.session-changed",
            "工作区文件已被其他赛事替换，请重新打开后再操作。");

    private WorkspaceCommandResult Publish(TournamentWorkspace workspace, string path, string? backupPath)
    {
        SetSession(new(workspace, path));
        var notices = workspace.Projects.SelectMany(p => p.Roster?.Warnings.Select(w => new WorkspaceNotice(w.Code, w.Message, p.Id)) ?? []).ToArray();
        return new(workspace, path, backupPath, notices);
    }

    private void RefreshAfterCommit(WorkspaceSession previous)
    {
        undoScheduleEdits = [];
        WorkspaceSession next;
        try { next = new(store.Read(previous.WorkspacePath), previous.WorkspacePath); }
        catch { next = previous with { RequiresReload = true }; }
        SetSession(next);
    }

    private void SetSession(WorkspaceSession session)
    {
        ReconcileScheduleUndo(session);
        Volatile.Write(ref currentSession, session);
        if (SessionChanged is not { } handlers) return;
        notifyingSession = true;
        try
        {
            foreach (EventHandler<WorkspaceSession> handler in handlers.GetInvocationList())
            {
                try { handler(this, session); }
                catch (Exception exception)
                {
                    // Observers cannot roll back publication or turn a saved command into a failed command.
                    try { Trace.TraceError("工作区界面刷新失败：{0}", exception); } catch { /* Logging is best effort. */ }
                }
            }
        }
        finally { notifyingSession = false; }
    }

    private void RequireOutsideNotification() => Require(!notifyingSession, "workspace.notification-busy",
        "工作区正在通知界面刷新，请在刷新结束后执行下一条命令。");

    private static ProjectRoster ReadRoster(string inputPath, EventDiscipline discipline)
    {
        string? temporary = null;
        try
        {
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)) throw new ExcelImportException("找不到参赛名单文件。");
            if (!string.Equals(Path.GetExtension(inputPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new ExcelImportException("当前仅支持 .xlsx 格式的参赛名单。");
            var bytes = File.ReadAllBytes(inputPath);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            temporary = Path.Combine(Path.GetTempPath(), "badminton-roster-" + Guid.NewGuid().ToString("N") + ".xlsx");
            using (var snapshot = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) snapshot.Write(bytes);
            var reader = new ParticipantExcelReader();
            var kind = EventKindFor(discipline);
            Require(reader.DetectEventKind(temporary, kind) == kind, "roster.discipline", "名单内容与所选项目不符，请检查单打、双打或团体格式。");
            var imported = reader.ReadParticipantsWithWarnings(temporary, kind);
            return new(imported.Participants, Path.GetFileName(inputPath), hash,
                imported.Warnings.Select(w => new ProjectRosterWarning(w.Kind switch
                {
                    ParticipantImportWarningKind.DuplicatePlayerName => "roster.duplicate-player-name",
                    ParticipantImportWarningKind.UnrankedSeed => "roster.unranked-seed",
                    _ => "roster.warning"
                }, w.Summary + "：" + w.Detail)).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new WorkspaceCommandException(new("roster.import", "无法读取参赛名单：" + exception.Message), exception); }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }

    private static EventKind EventKindFor(EventDiscipline discipline) => discipline switch
    {
        EventDiscipline.Team => EventKind.Team,
        EventDiscipline.MenDoubles or EventDiscipline.WomenDoubles or EventDiscipline.MixedDoubles => EventKind.Doubles,
        _ => EventKind.Singles
    };
    private static TournamentProject NewProject(WorkspaceProjectRequest request, int order) =>
        TournamentProject.Create(request.Discipline, request.CompetitionMode, order, request.DisplayName?.Trim());
    private static TournamentProject Project(TournamentWorkspace workspace, Guid id) =>
        workspace.Projects.SingleOrDefault(p => p.Id == id) ?? throw new WorkspaceValidationException("project.not-found", "当前工作区中找不到该项目。");
    private static TournamentProject EditableProject(TournamentWorkspace workspace, Guid id)
    {
        var project = Project(workspace, id);
        Require(project.Draw?.ConfirmedAt is null, "draw.frozen", "抽签已确认，请先明确解除确认后再修改。");
        return project;
    }
    private static TournamentWorkspace ReplaceProject(TournamentWorkspace workspace, TournamentProject project) =>
        workspace with { Projects = workspace.Projects.Select(p => p.Id == project.Id ? project : p).ToArray() };
    private static TournamentWorkspace Audit(TournamentWorkspace workspace, string action, Guid? projectId = null, string detail = "") =>
        workspace with { AuditEvents = [.. workspace.AuditEvents, new(Guid.NewGuid(), action, DateTimeOffset.UtcNow, projectId, Detail: detail)] };
    private static void Require(bool condition, string code, string message)
    {
        if (!condition) throw new WorkspaceValidationException(code, message);
    }
}
