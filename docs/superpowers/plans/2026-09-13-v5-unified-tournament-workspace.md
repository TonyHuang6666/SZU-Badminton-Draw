# v5.0 Unified Tournament Workspace Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the v4 single-project/archive-first flow with one v5 `.szbd` workspace that supports a mutually exclusive team tournament or one-to-five individual events, an explicit public-draw stopping point, and one global scheduling pass.

**Architecture:** Keep the pure algorithms in `BadmintonDraw.Core`, add `BadmintonDraw.Persistence` for the new SQLite workspace, retain file rendering in `BadmintonDraw.Excel`, orchestrate state-changing commands in `BadmintonDraw.Workflows`, and replace the monolithic Avalonia window with a stage-gated shell and focused pages. Draw output becomes a time-free `MatchGraph`; the unified scheduler stores only `MatchPlacement` values for graph matches.

**Tech Stack:** C# / .NET 10, Avalonia 12, SQLite through `Microsoft.Data.Sqlite`, ClosedXML, SkiaSharp, xUnit, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-13-v5-unified-tournament-workspace-design.md`

## Global Constraints

- Start implementation from the reviewed planning commit on `main` (whose product-code parent is tag `v4.6.0`) in an isolated worktree and branch named `feature/v5-unified-workspace`.
- Set every project to `net10.0`; pin SDK `10.0.301` with `rollForward: latestPatch`.
- Do not implement reading, importing, or migrating v4 `.szbd` files.
- A workspace is either `Individual` or `Team`; the two kinds never coexist in one file.
- A team workspace contains exactly one `Team` project; an individual workspace contains one to five unique disciplines.
- Participant import never triggers a draw; draw confirmation never triggers scheduling.
- A multi-project workspace never creates project-level dates, courts, or scheduling policies.
- Every successful state-changing command increments `TournamentWorkspace.Revision` and atomically saves the unified file.
- Do not add an external MVVM package; use small local command and observable base classes.
- Keep the existing v4 behavior available only through the published v4.6.0 binary, not through v5 code paths.
- Run the dependency vulnerability audit and the complete test suite at every milestone gate.
- Before each commit, inspect `git status` and stage only the files owned by that task.

---

### Task 1: Establish the .NET 10 build baseline and Persistence project

**Files:**
- Modify: `global.json`
- Modify: `Directory.Build.props`
- Modify: `.github/workflows/ci.yml`
- Modify: `BadmintonDraw.sln`
- Create: `src/BadmintonDraw.Persistence/BadmintonDraw.Persistence.csproj`
- Create: `src/BadmintonDraw.Persistence/WorkspaceSchemaVersion.cs`
- Modify: `tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj`
- Create: `tests/BadmintonDraw.Tests/BuildBaselineTests.cs`
- Regenerate: every tracked `packages.lock.json`

**Interfaces:**
- Produces: `WorkspaceSchemaVersion.Current == 500` for all later persistence work.
- Consumes: no new application interfaces.

- [ ] **Step 1: Write the failing baseline test**

```csharp
using BadmintonDraw.Persistence;

namespace BadmintonDraw.Tests;

public sealed class BuildBaselineTests
{
    [Fact]
    public void V5SchemaVersionIs500()
    {
        Assert.Equal(500, WorkspaceSchemaVersion.Current);
    }
}
```

- [ ] **Step 2: Run the test and confirm the missing project/type failure**

Run:

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter FullyQualifiedName~BuildBaselineTests --verbosity minimal
```

Expected: compilation fails because `BadmintonDraw.Persistence` and `WorkspaceSchemaVersion` do not exist.

- [ ] **Step 3: Add the project and exact schema constant**

```csharp
namespace BadmintonDraw.Persistence;

public static class WorkspaceSchemaVersion
{
    public const int Current = 500;
}
```

The Persistence project references `BadmintonDraw.Core` and packages `Microsoft.Data.Sqlite` `8.0.24` plus `SQLitePCLRaw.lib.e_sqlite3` `2.1.13`. Add Persistence references to the existing test and Workflows projects.

- [ ] **Step 4: Move the solution and CI to .NET 10**

Set `TargetFramework` to `net10.0` in all six existing projects and the new Persistence project. Set `global.json` to:

```json
{
  "sdk": {
    "version": "10.0.301",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

Change both CI setup steps to `dotnet-version: 10.0.x`, regenerate lock files with `dotnet restore BadmintonDraw.sln --force-evaluate`, and keep the vulnerability audit unchanged.

- [ ] **Step 5: Run the baseline gate**

Run:

```bash
dotnet --version
dotnet restore BadmintonDraw.sln --locked-mode
scripts/check-vulnerable-packages.sh BadmintonDraw.sln
dotnet build BadmintonDraw.sln -c Release --no-restore --verbosity minimal
dotnet test BadmintonDraw.sln -c Release --no-build --verbosity minimal
```

Expected: SDK begins with `10.0`, audit reports no vulnerable packages, build has zero errors, and the complete existing suite plus `BuildBaselineTests` passes.

- [ ] **Step 6: Commit the baseline**

```bash
git add global.json Directory.Build.props .github/workflows/ci.yml BadmintonDraw.sln src/BadmintonDraw.Persistence tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj tests/BadmintonDraw.Tests/BuildBaselineTests.cs src/*/packages.lock.json tests/*/packages.lock.json tools/*/packages.lock.json
git commit -m "build: establish .NET 10 v5 baseline"
```

---

### Task 2: Add the workspace aggregate and enforce its state machine

**Files:**
- Create: `src/BadmintonDraw.Core/Tournaments/TournamentKind.cs`
- Create: `src/BadmintonDraw.Core/Tournaments/TournamentPurpose.cs`
- Create: `src/BadmintonDraw.Core/Tournaments/TournamentStage.cs`
- Create: `src/BadmintonDraw.Core/Tournaments/EventDiscipline.cs`
- Create: `src/BadmintonDraw.Core/Tournaments/TournamentProject.cs`
- Create: `src/BadmintonDraw.Core/Tournaments/TournamentWorkspace.cs`
- Create: `src/BadmintonDraw.Core/Tournaments/WorkspaceMatchKey.cs`
- Create: `src/BadmintonDraw.Core/Tournaments/TournamentMatchResult.cs`
- Create: `src/BadmintonDraw.Core/Tournaments/WorkspaceAuditEvent.cs`
- Create: `src/BadmintonDraw.Core/Tournaments/TournamentWorkspaceRules.cs`
- Create: `src/BadmintonDraw.Core/Tournaments/WorkspaceValidationException.cs`
- Create: `tests/BadmintonDraw.Tests/TournamentWorkspaceRulesTests.cs`

**Interfaces:**
- Produces: immutable `TournamentWorkspace`, `TournamentProject`, project-qualified results/audit events, enums, and `TournamentWorkspaceRules.Validate/Transition`.
- Consumes: existing `CompetitionMode`, `DrawParticipant`, `DrawResult` types.

- [ ] **Step 1: Write failing construction and transition tests**

```csharp
[Fact]
public void TeamWorkspaceRejectsIndividualProject()
{
    var project = TournamentProject.Create(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, 0);
    Assert.Throws<WorkspaceValidationException>(() =>
        TournamentWorkspace.Create("校长杯", TournamentKind.Team, TournamentPurpose.FullTournament, [project]));
}

[Fact]
public void IndividualWorkspaceRejectsDuplicateDisciplines()
{
    var first = TournamentProject.Create(EventDiscipline.MixedDoubles, CompetitionMode.SinglesKnockout, 0);
    var second = TournamentProject.Create(EventDiscipline.MixedDoubles, CompetitionMode.SinglesKnockout, 1);
    Assert.Throws<WorkspaceValidationException>(() =>
        TournamentWorkspace.Create("新生杯", TournamentKind.Individual, TournamentPurpose.PublicDrawOnly, [first, second]));
}

[Fact]
public void WorkspaceCannotSkipFromDraftToDrawsConfirmed()
{
    var workspace = WorkspaceTestData.CreateIndividualWorkspace();
    Assert.Throws<WorkspaceValidationException>(() =>
        TournamentWorkspaceRules.Transition(workspace, TournamentStage.DrawsConfirmed));
}
```

- [ ] **Step 2: Run the tests and confirm all three fail for missing domain types**

Run:

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter FullyQualifiedName~TournamentWorkspaceRulesTests --verbosity minimal
```

Expected: compilation fails on `TournamentWorkspace`.

- [ ] **Step 3: Implement the immutable aggregate and rules**

Use these exact enums:

```csharp
public enum TournamentKind { Individual = 1, Team = 2 }
public enum TournamentPurpose { PublicDrawOnly = 1, FullTournament = 2 }
public enum TournamentStage { Draft = 1, RostersReady = 2, DrawsConfirmed = 3, ScheduleReady = 4, InProgress = 5, Completed = 6 }
public enum EventDiscipline { MenSingles = 1, WomenSingles = 2, MenDoubles = 3, WomenDoubles = 4, MixedDoubles = 5, Team = 6 }
```

`TournamentWorkspace.Create` assigns new GUIDs, UTC timestamps, `Draft`, and revision `0`. `Validate` enforces the Global Constraints. `Transition` permits only adjacent forward transitions, plus `PublicDrawOnly → FullTournament` as a purpose change while preserving `DrawsConfirmed`.

- [ ] **Step 4: Add transition matrix tests**

Cover all allowed and rejected pairs, including: `DrawsConfirmed → ScheduleReady` only for `FullTournament`; `ScheduleReady → InProgress` only with at least one result; `InProgress → Completed` only when every required match has a result.

- [ ] **Step 5: Run the focused and full test suites**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter FullyQualifiedName~TournamentWorkspaceRulesTests --verbosity minimal
dotnet test BadmintonDraw.sln -c Release --verbosity minimal
```

Expected: all workspace rule tests and the full suite pass.

- [ ] **Step 6: Commit the domain state machine**

```bash
git add src/BadmintonDraw.Core/Tournaments tests/BadmintonDraw.Tests/TournamentWorkspaceRulesTests.cs
git commit -m "feat: add v5 tournament workspace state machine"
```

---

### Task 3: Separate MatchGraph structure from schedule placement

**Files:**
- Create: `src/BadmintonDraw.Core/Matches/EntrantSource.cs`
- Create: `src/BadmintonDraw.Core/Matches/MatchNode.cs`
- Create: `src/BadmintonDraw.Core/Matches/MatchGraph.cs`
- Create: `src/BadmintonDraw.Core/Matches/MatchGraphFactory.cs`
- Create: `src/BadmintonDraw.Core/Scheduling/MatchPlacement.cs`
- Create: `src/BadmintonDraw.Core/Scheduling/TournamentSchedule.cs`
- Create: `src/BadmintonDraw.Core/Scheduling/ScheduledMatchProjection.cs`
- Create: `tests/BadmintonDraw.Tests/MatchGraphTests.cs`
- Create: `tests/BadmintonDraw.Tests/ScheduledMatchProjectionTests.cs`

**Interfaces:**
- Produces: `MatchGraphFactory.Create(Guid projectId, DrawResult draw)`, `TournamentSchedule`, and `ScheduledMatchProjection.Build(...)`.
- Consumes: existing `DrawResult`, `ScheduleMatchDependency`, and the Core-owned `TournamentMatchResult` from Task 2.

- [ ] **Step 1: Write failing tests proving the graph has no placement data**

```csharp
[Fact]
public void KnockoutGraphUsesExplicitWinnerSourcesAndNoTimeFields()
{
    var draw = DrawTestData.CreateEightPlayerKnockout();
    var graph = MatchGraphFactory.Create(Guid.Parse("11111111-1111-1111-1111-111111111111"), draw);

    Assert.Contains(graph.Matches, match => match.SideA is EntrantSource.WinnerOf);
    Assert.DoesNotContain(typeof(MatchNode).GetProperties(), property =>
        property.Name is "DayLabel" or "StartTime" or "EndTime" or "Court");
}

[Fact]
public void GraphMatchIdsAreStableForTheSameDraw()
{
    var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var draw = DrawTestData.CreateEightPlayerKnockout();
    Assert.Equal(
        MatchGraphFactory.Create(projectId, draw).Matches.Select(match => match.Id),
        MatchGraphFactory.Create(projectId, draw).Matches.Select(match => match.Id));
}
```

- [ ] **Step 2: Run and observe the missing graph failures**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter "FullyQualifiedName~MatchGraphTests|FullyQualifiedName~ScheduledMatchProjectionTests" --verbosity minimal
```

Expected: compilation fails on `MatchGraphFactory`.

- [ ] **Step 3: Implement explicit entrant sources**

```csharp
public abstract record EntrantSource
{
    public sealed record Participant(string IdentityKey, string DisplayName) : EntrantSource;
    public sealed record WinnerOf(Guid MatchId) : EntrantSource;
    public sealed record LoserOf(Guid MatchId) : EntrantSource;
    public sealed record Bye : EntrantSource;
}
```

`MatchNode` contains `Id`, `ProjectId`, order, group, phase, display name, both entrant sources, expected duration, and dependency identifiers. Generate deterministic match GUIDs from `projectId + existing MatchId` using SHA-256 truncated to 16 bytes.

- [ ] **Step 4: Implement placement and projection**

```csharp
public sealed record MatchPlacement(
    Guid MatchId,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Court);

public sealed record TournamentSchedule(
    IReadOnlyDictionary<Guid, MatchPlacement> Placements,
    TournamentResourcePlan Resources,
    TournamentSchedulingPolicy Policy,
    long Revision);
```

The projection resolves participant, winner, and loser labels from the graph plus current results and returns the existing export-facing `ScheduledMatch` model during migration.

- [ ] **Step 5: Add graph integrity tests**

Assert unique IDs, same-project dependencies, acyclic order, valid winner/loser references, correct round-robin match count, and third-to-eighth placement branches.

- [ ] **Step 6: Run graph, draw, and schedule regression suites**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter "FullyQualifiedName~MatchGraph|FullyQualifiedName~Draw|FullyQualifiedName~Schedule" --verbosity minimal
```

Expected: all selected tests pass.

- [ ] **Step 7: Commit the graph boundary**

```bash
git add src/BadmintonDraw.Core/Matches src/BadmintonDraw.Core/Scheduling tests/BadmintonDraw.Tests/MatchGraphTests.cs tests/BadmintonDraw.Tests/ScheduledMatchProjectionTests.cs
git commit -m "refactor: separate match graphs from placements"
```

---

### Task 4: Implement the v5 unified SQLite workspace store

**Files:**
- Create: `src/BadmintonDraw.Persistence/ITournamentWorkspaceStore.cs`
- Create: `src/BadmintonDraw.Persistence/TournamentWorkspaceStore.cs`
- Create: `src/BadmintonDraw.Persistence/WorkspaceDatabaseSchema.cs`
- Create: `src/BadmintonDraw.Persistence/WorkspaceSerializer.cs`
- Create: `src/BadmintonDraw.Persistence/WorkspaceStoreException.cs`
- Create: `src/BadmintonDraw.Persistence/WorkspaceMutation.cs`
- Create: `tests/BadmintonDraw.Tests/TournamentWorkspaceStoreTests.cs`
- Create: `tests/BadmintonDraw.Tests/TournamentWorkspaceStoreFailureTests.cs`

**Interfaces:**
- Produces: `Create`, `Read`, `Mutate`, `CreateBackup`, and `RestoreBackup` on `ITournamentWorkspaceStore`.
- Consumes: `TournamentWorkspace`, `WorkspaceSchemaVersion.Current`, workspace rules.

- [ ] **Step 1: Write failing round-trip and no-schedule tests**

```csharp
[Fact]
public void DrawOnlyWorkspaceRoundTripsWithoutSchedule()
{
    using var temp = TempDirectory.Create();
    var path = temp.File("公开抽签.szbd");
    var store = new TournamentWorkspaceStore();
    var expected = WorkspaceTestData.CreateConfirmedDrawOnlyWorkspace();

    store.Create(path, expected);
    var actual = store.Read(path);

    Assert.Equal(TournamentStage.DrawsConfirmed, actual.Stage);
    Assert.Null(actual.Schedule);
    Assert.Equal(expected.Projects.Select(project => project.Id), actual.Projects.Select(project => project.Id));
}
```

- [ ] **Step 2: Run and confirm failure because the store does not exist**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter FullyQualifiedName~TournamentWorkspaceStoreTests --verbosity minimal
```

Expected: compilation fails on `TournamentWorkspaceStore`.

- [ ] **Step 3: Create the exact v5 schema**

`WorkspaceDatabaseSchema.Initialize` sets `PRAGMA user_version = 500`, `journal_mode = DELETE`, and `foreign_keys = ON`, then creates every table named in spec section 9. Primary keys are workspace GUID text, project GUID text, and `(project_id, match_id)` for results. JSON columns are `TEXT NOT NULL`; optional aggregate rows are absent rather than storing empty strings.

- [ ] **Step 4: Implement candidate-copy mutation**

```csharp
public interface ITournamentWorkspaceStore
{
    TournamentWorkspace Create(string path, TournamentWorkspace workspace);
    TournamentWorkspace Read(string path);
    WorkspaceMutationResult Mutate(
        string path,
        long expectedRevision,
        Func<TournamentWorkspace, TournamentWorkspace> mutation);
    string CreateBackup(string path);
    TournamentWorkspace RestoreBackup(string path, string backupPath);
}
```

`Mutate` must copy to `.<name>.<guid>.candidate.szbd`, run one transaction, increment revision exactly once, validate integrity and domain rules, create a backup, atomically replace, and re-read.

- [ ] **Step 5: Write and run failure-injection tests**

Tests must cover candidate transaction failure, post-transaction semantic failure, replacement failure, corrupt database, missing required row, and stale `expectedRevision`. Each test compares the original file hash before and after failure and checks the reported backup/candidate path.

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter "FullyQualifiedName~TournamentWorkspaceStore" --verbosity minimal
```

Expected: every failure leaves the formal workspace readable and unchanged.

- [ ] **Step 6: Add explicit v4 rejection**

Create a schema-1 SQLite fixture and assert `Read` throws `WorkspaceStoreException` with error code `UnsupportedWorkspaceVersion` and a message containing `v4.6.0` and its Release URL. Do not call `TournamentProgressStore` or `ScheduleDependencyBackfill`.

- [ ] **Step 7: Run the persistence and full-suite gates**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter "FullyQualifiedName~TournamentWorkspaceStore" --verbosity minimal
scripts/check-vulnerable-packages.sh BadmintonDraw.sln
dotnet test BadmintonDraw.sln -c Release --verbosity minimal
```

- [ ] **Step 8: Commit the unified store**

```bash
git add src/BadmintonDraw.Persistence tests/BadmintonDraw.Tests/TournamentWorkspaceStoreTests.cs tests/BadmintonDraw.Tests/TournamentWorkspaceStoreFailureTests.cs
git commit -m "feat: add unified v5 workspace persistence"
```

---

### Task 5: Add command-oriented workspace workflows and automatic save

**Files:**
- Create: `src/BadmintonDraw.Workflows/Tournaments/TournamentWorkspaceWorkflow.cs`
- Create: `src/BadmintonDraw.Workflows/Tournaments/WorkspaceSession.cs`
- Create: `src/BadmintonDraw.Workflows/Tournaments/WorkspaceRequests.cs`
- Create: `src/BadmintonDraw.Workflows/Tournaments/WorkspaceCommandResult.cs`
- Create: `src/BadmintonDraw.Workflows/Tournaments/WorkspaceError.cs`
- Modify: `src/BadmintonDraw.Workflows/BadmintonDraw.Workflows.csproj`
- Create: `tests/BadmintonDraw.Tests/TournamentWorkspaceWorkflowTests.cs`

**Interfaces:**
- Produces: the command surface listed in spec section 10 and a current immutable `WorkspaceSession`.
- Consumes: `ITournamentWorkspaceStore`, `DrawService`, `MatchGraphFactory`, unified scheduler port, and Excel adapters.

- [ ] **Step 1: Write failing workflow sequencing tests**

```csharp
[Fact]
public void ImportRosterDoesNotGenerateDrawOrSchedule()
{
    var session = WorkspaceWorkflowTestData.CreateDraftSession();
    var updated = session.Workflow.ImportRoster(session.ProjectId, session.InputPath, session.Revision).Workspace;

    Assert.NotNull(updated.Projects.Single().Roster);
    Assert.Null(updated.Projects.Single().Draw);
    Assert.Null(updated.Projects.Single().MatchGraph);
    Assert.Null(updated.Schedule);
}

[Fact]
public void ConfirmLastDrawStopsAtDrawsConfirmed()
{
    var session = WorkspaceWorkflowTestData.CreatePreviewedDrawOnlySession();
    var updated = session.Workflow.ConfirmDraw(session.ProjectId, session.Revision).Workspace;

    Assert.Equal(TournamentStage.DrawsConfirmed, updated.Stage);
    Assert.Null(updated.Schedule);
}
```

- [ ] **Step 2: Run and observe missing workflow failures**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter FullyQualifiedName~TournamentWorkspaceWorkflowTests --verbosity minimal
```

- [ ] **Step 3: Implement command results and revision handling**

```csharp
public sealed record WorkspaceCommandResult(
    TournamentWorkspace Workspace,
    string WorkspacePath,
    string? BackupPath,
    IReadOnlyList<WorkspaceNotice> Notices);
```

Every state-changing method passes `expectedRevision` to `ITournamentWorkspaceStore.Mutate`. No command calls a second state-changing command internally. In particular, `ImportRoster` stops after roster persistence and `ConfirmDraw` stops after graph persistence.

- [ ] **Step 4: Implement downstream invalidation rules**

`ReopenDraw` requires a non-empty reason, rejects any workspace with results, clears the selected project draw/graph, clears the global schedule/resources, returns the stage to `RostersReady`, and appends an audit event. `UpgradeToFullTournament` changes only purpose and revision.

- [ ] **Step 5: Run state, persistence, and workflow tests**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter "FullyQualifiedName~TournamentWorkspace" --verbosity minimal
dotnet test BadmintonDraw.sln -c Release --verbosity minimal
```

- [ ] **Step 6: Commit the command boundary**

```bash
git add src/BadmintonDraw.Workflows/Tournaments src/BadmintonDraw.Workflows/BadmintonDraw.Workflows.csproj tests/BadmintonDraw.Tests/TournamentWorkspaceWorkflowTests.cs
git commit -m "feat: add autosaving workspace commands"
```

---

### Task 6: Add an Avalonia shell and creation wizard alongside the legacy window

**Files:**
- Create: `src/BadmintonDraw.Desktop/ViewModels/ViewModelBase.cs`
- Create: `src/BadmintonDraw.Desktop/ViewModels/DelegateCommand.cs`
- Create: `src/BadmintonDraw.Desktop/ViewModels/AsyncCommand.cs`
- Create: `src/BadmintonDraw.Desktop/Navigation/WorkspaceRoute.cs`
- Create: `src/BadmintonDraw.Desktop/Navigation/WorkspaceNavigator.cs`
- Create: `src/BadmintonDraw.Desktop/ViewModels/AppShellViewModel.cs`
- Create: `src/BadmintonDraw.Desktop/ViewModels/StartPageViewModel.cs`
- Create: `src/BadmintonDraw.Desktop/ViewModels/NewWorkspaceWizardViewModel.cs`
- Create: `src/BadmintonDraw.Desktop/Views/StartPage.axaml`
- Create: `src/BadmintonDraw.Desktop/Views/NewWorkspaceWizardPage.axaml`
- Create: `src/BadmintonDraw.Desktop/Views/WorkspaceOverviewPage.axaml`
- Create: `src/BadmintonDraw.Desktop/AppShellWindow.axaml`
- Create: `src/BadmintonDraw.Desktop/AppShellWindow.axaml.cs`
- Modify: `src/BadmintonDraw.Desktop/App.axaml.cs`
- Create: `tests/BadmintonDraw.Desktop.Tests/BadmintonDraw.Desktop.Tests.csproj`
- Create: `tests/BadmintonDraw.Desktop.Tests/NewWorkspaceWizardViewModelTests.cs`
- Modify: `BadmintonDraw.sln`

**Interfaces:**
- Produces: `WorkspaceNavigator.CurrentRoute`, shell `CurrentPage`, and wizard `CreateRequest`.
- Consumes: `TournamentWorkspaceWorkflow.CreateWorkspace/OpenWorkspace`.

- [ ] **Step 1: Write failing wizard decision tests**

```csharp
[Fact]
public void ChoosingTeamCreatesExactlyOneTeamProject()
{
    var viewModel = NewWorkspaceWizardViewModelTestData.Create();
    viewModel.SelectKind(TournamentKind.Team);
    viewModel.SelectPurpose(TournamentPurpose.PublicDrawOnly);

    var request = viewModel.BuildRequest("校长杯团体赛", "/tmp/team.szbd");

    Assert.Single(request.Projects);
    Assert.Equal(EventDiscipline.Team, request.Projects[0].Discipline);
}

[Fact]
public void ChoosingMultipleIndividualEventsCreatesNoScheduleSettings()
{
    var viewModel = NewWorkspaceWizardViewModelTestData.Create();
    viewModel.SelectKind(TournamentKind.Individual);
    viewModel.SetDisciplines([EventDiscipline.MenSingles, EventDiscipline.MenDoubles, EventDiscipline.MixedDoubles]);

    var request = viewModel.BuildRequest("校长杯单项赛", "/tmp/individual.szbd");

    Assert.Equal(3, request.Projects.Count);
    Assert.Null(request.ResourcePlan);
    Assert.Null(request.SchedulingPolicy);
}
```

- [ ] **Step 2: Run and confirm the ViewModel project/type failures**

```bash
dotnet test tests/BadmintonDraw.Desktop.Tests/BadmintonDraw.Desktop.Tests.csproj --filter FullyQualifiedName~NewWorkspaceWizardViewModelTests --verbosity minimal
```

- [ ] **Step 3: Implement local MVVM primitives and route gating**

`WorkspaceNavigator` maps `Draft` to overview/rosters, `RostersReady` to public draw, `DrawsConfirmed + PublicDrawOnly` to public draw, `DrawsConfirmed + FullTournament` to schedule setup, `ScheduleReady` to schedule board, and `InProgress/Completed` to operations. It permits backward read-only navigation but rejects forward routes whose stage is not reached.

- [ ] **Step 4: Implement the three-step wizard**

Step 1 selects name, kind, and purpose. Step 2 shows either one team project or checkboxes for the five individual disciplines. Step 3 selects `.szbd` path and summarizes choices. The Create button calls `CreateWorkspace` once and navigates to overview; it does not import a roster.

- [ ] **Step 5: Launch the new shell without breaking legacy compilation**

`AppShellWindow` contains the app header, stage navigation, `ContentControl` page host, and global status bar. Change `App.axaml.cs` to launch it, but leave the old `MainWindow` and its partials compiling and unused until Task 11 deletes the complete legacy surface. Keep file pickers in small view code-behind classes. Do not copy the old 1,195-line two-column tab layout into the shell.

- [ ] **Step 6: Run desktop ViewModel and complete tests**

```bash
dotnet test tests/BadmintonDraw.Desktop.Tests/BadmintonDraw.Desktop.Tests.csproj --verbosity minimal
dotnet test BadmintonDraw.sln -c Release --verbosity minimal
```

- [ ] **Step 7: Commit the new shell**

```bash
git add BadmintonDraw.sln src/BadmintonDraw.Desktop tests/BadmintonDraw.Desktop.Tests
git commit -m "feat: add v5 workspace shell and creation wizard"
```

---

### Task 7: Build project roster and public draw pages

**Files:**
- Create: `src/BadmintonDraw.Desktop/ViewModels/RostersPageViewModel.cs`
- Create: `src/BadmintonDraw.Desktop/ViewModels/ProjectRosterViewModel.cs`
- Create: `src/BadmintonDraw.Desktop/ViewModels/PublicDrawPageViewModel.cs`
- Create: `src/BadmintonDraw.Desktop/ViewModels/ProjectDrawViewModel.cs`
- Create: `src/BadmintonDraw.Desktop/Views/RostersPage.axaml`
- Create: `src/BadmintonDraw.Desktop/Views/PublicDrawPage.axaml`
- Create: `src/BadmintonDraw.Workflows/Tournaments/DrawPackageWorkflow.cs`
- Modify: `src/BadmintonDraw.Excel/DrawResultExcelWriter.cs`
- Modify: `src/BadmintonDraw.Excel/DrawResultVisualWriter.cs`
- Create: `tests/BadmintonDraw.Desktop.Tests/PublicDrawPageViewModelTests.cs`
- Create: `tests/BadmintonDraw.Tests/DrawPackageWorkflowTests.cs`

**Interfaces:**
- Produces: explicit `PreviewDrawCommand`, `ExportPreviewCommand`, `ConfirmDrawCommand`, and workspace-aware draw package export.
- Consumes: Task 5 commands and Task 3 graph factory.

- [ ] **Step 1: Write failing tests for explicit public draw actions**

```csharp
[Fact]
public async Task ImportingRosterLeavesDrawButtonReadyButDoesNotRunIt()
{
    var fixture = PublicDrawViewModelTestData.CreateWithImportedRoster();
    await fixture.ViewModel.LoadAsync();

    Assert.True(fixture.ViewModel.PreviewDrawCommand.CanExecute(null));
    Assert.Null(fixture.Session.Workspace.Projects.Single().Draw);
    Assert.Equal(0, fixture.DrawServiceCallCount);
}

[Fact]
public async Task ConfirmRequiresPreviewAndPreservesExportableAudit()
{
    var fixture = PublicDrawViewModelTestData.CreateWithImportedRoster();
    await fixture.ViewModel.PreviewDrawCommand.ExecuteAsync(null);
    await fixture.ViewModel.ConfirmDrawCommand.ExecuteAsync(null);

    Assert.NotNull(fixture.ViewModel.SelectedProject.DrawAudit);
    Assert.Equal(TournamentStage.DrawsConfirmed, fixture.Session.Workspace.Stage);
}
```

- [ ] **Step 2: Run and confirm missing page/workflow failures**

```bash
dotnet test tests/BadmintonDraw.Desktop.Tests/BadmintonDraw.Desktop.Tests.csproj --filter FullyQualifiedName~PublicDrawPageViewModelTests --verbosity minimal
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter FullyQualifiedName~DrawPackageWorkflowTests --verbosity minimal
```

- [ ] **Step 3: Implement roster cards and readiness**

Each project card shows source filename, participant count, warnings, and “replace roster”. All cards must be valid before stage becomes `RostersReady`. Replacing any roster before confirmation clears only that project's unconfirmed draw preview.

- [ ] **Step 4: Implement the public draw session**

Each project tab exposes draw settings, visible random seed, participant hash, Preview, Export Preview, Confirm, and Export Confirmed Result. Multi-project confirmation advances the workspace only when every project is confirmed. No handler calls `GenerateSchedule`.

- [ ] **Step 5: Add draw-only close/reopen/upgrade coverage**

Create a real `.szbd`, confirm all draws, export Excel and PDF, close the store, reopen, assert no schedule row exists, then call `UpgradeToFullTournament` and assert purpose changes while draw hashes and graphs remain byte-for-byte equal.

- [ ] **Step 6: Run draw, export, persistence, and desktop tests**

```bash
dotnet test BadmintonDraw.sln -c Release --verbosity minimal
```

- [ ] **Step 7: Commit the public draw milestone**

```bash
git add src/BadmintonDraw.Desktop src/BadmintonDraw.Workflows/Tournaments src/BadmintonDraw.Excel tests
git commit -m "feat: add staged roster and public draw workflow"
```

---

### Task 8: Replace single/cross-event schedulers with one tournament scheduler

**Files:**
- Create: `src/BadmintonDraw.Core/Scheduling/TournamentResourcePlan.cs`
- Create: `src/BadmintonDraw.Core/Scheduling/TournamentSchedulingPolicy.cs`
- Create: `src/BadmintonDraw.Core/Scheduling/TournamentSchedulingRequest.cs`
- Create: `src/BadmintonDraw.Core/Scheduling/TournamentSchedulingResult.cs`
- Create: `src/BadmintonDraw.Core/Scheduling/TournamentScheduler.cs`
- Create: `src/BadmintonDraw.Core/Scheduling/SchedulingFailure.cs`
- Refactor: `src/BadmintonDraw.Core/ScheduleService.cs`
- Refactor: `src/BadmintonDraw.Core/ScheduleConstraintAnalyzer.cs`
- Refactor: `src/BadmintonDraw.Core/PlayerLoadForecastAnalyzer.cs`
- Modify: `src/BadmintonDraw.Workflows/Tournaments/TournamentWorkspaceWorkflow.cs`
- Create: `tests/BadmintonDraw.Tests/TournamentSchedulerTests.cs`
- Create: `tests/BadmintonDraw.Tests/TournamentSchedulingFailureTests.cs`

**Interfaces:**
- Produces: `TournamentScheduler.Generate(TournamentSchedulingRequest)` returning success with one `TournamentSchedule` or a structured failure.
- Consumes: one or more `MatchGraph` instances and one global resource plan.

- [ ] **Step 1: Write failing single/multi unification tests**

```csharp
[Fact]
public void SingleAndMultiProjectRequestsUseTheSameSchedulerResultType()
{
    var single = TournamentSchedulerTestData.SingleProjectRequest();
    var multiple = TournamentSchedulerTestData.ThreeProjectRequest();

    Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(single));
    Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(multiple));
}

[Fact]
public void MultiProjectScheduleContainsOneGlobalPolicyAndNoProjectSchedules()
{
    var request = TournamentSchedulerTestData.ThreeProjectRequest();
    var result = Assert.IsType<TournamentSchedulingResult.Success>(
        new TournamentScheduler().Generate(request));

    Assert.Equal(ScheduleAutoSchedulingStrategy.BalancedRelaxed, result.Schedule.Policy.Strategy);
    Assert.Equal(3, result.GraphRevisions.Count);
    Assert.Equal(
        request.MatchGraphs.Sum(graph => graph.Matches.Count),
        result.Schedule.Placements.Count);
}
```

- [ ] **Step 2: Run and confirm missing unified scheduler failures**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter FullyQualifiedName~TournamentSchedul --verbosity minimal
```

- [ ] **Step 3: Extract reusable placement primitives from current schedulers**

Move slot generation, dependency readiness, candidate identity propagation, resource capacity checks, scoring, and quality report generation behind methods that accept graph nodes and current placements. Keep the proven compatible winner/loser path conditions from v4.6.

- [ ] **Step 4: Implement structured failure without partial persistence**

```csharp
public abstract record TournamentSchedulingResult
{
    public sealed record Success(
        TournamentSchedule Schedule,
        IReadOnlyDictionary<Guid, string> GraphRevisions) : TournamentSchedulingResult;

    public sealed record Failure(SchedulingFailure Detail) : TournamentSchedulingResult;
}
```

`SchedulingFailure` includes unplaced match IDs, project names, violated constraint codes, capacity summary, and user-facing adjustment suggestions. The workflow only calls store mutation for `Success`.

- [ ] **Step 5: Port every v4 constraint regression to graph/placement input**

Cover courts, referee capacity, unavailable slots, confirmed player overlap, conditional candidate paths, rest, daily maximum, dependency order, compact/balanced/finals/custom policies, and the 4-hour safe-failure/6-hour success acceptance boundary.

- [ ] **Step 6: Run scheduler and full regression gates**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter "FullyQualifiedName~TournamentSchedul|FullyQualifiedName~ScheduleConstraint|FullyQualifiedName~PlayerLoad" --verbosity minimal
dotnet test BadmintonDraw.sln -c Release --verbosity minimal
```

- [ ] **Step 7: Commit the unified scheduler**

```bash
git add src/BadmintonDraw.Core src/BadmintonDraw.Workflows/Tournaments tests/BadmintonDraw.Tests
git commit -m "refactor: unify single and multi-project scheduling"
```

---

### Task 9: Add global schedule setup and one reusable schedule board

**Files:**
- Create: `src/BadmintonDraw.Desktop/ViewModels/ScheduleSetupPageViewModel.cs`
- Create: `src/BadmintonDraw.Desktop/ViewModels/ScheduleBoardPageViewModel.cs`
- Create: `src/BadmintonDraw.Desktop/Views/ScheduleSetupPage.axaml`
- Create: `src/BadmintonDraw.Desktop/Views/ScheduleBoardPage.axaml`
- Create: `src/BadmintonDraw.Desktop/Controls/ScheduleBoardControl.axaml`
- Create: `src/BadmintonDraw.Desktop/Controls/ScheduleBoardControl.axaml.cs`
- Create: `src/BadmintonDraw.Workflows/Tournaments/ScheduleEditingWorkflow.cs`
- Create: `tests/BadmintonDraw.Desktop.Tests/ScheduleSetupPageViewModelTests.cs`
- Create: `tests/BadmintonDraw.Tests/ScheduleEditingWorkflowTests.cs`

**Interfaces:**
- Produces: one setup request per workspace and one board/editing model for any project count.
- Consumes: unified scheduler, `TournamentSchedule`, current results, and workspace commands.

- [ ] **Step 1: Write failing UI semantics tests**

```csharp
[Fact]
public void MultiProjectSetupShowsOneGlobalPolicyAndNoProjectPolicyCollection()
{
    var viewModel = ScheduleSetupViewModelTestData.CreateThreeProjectWorkspace();

    Assert.Equal("全赛事编排策略", viewModel.PolicyLabel);
    Assert.False(viewModel.ShowPerProjectSchedulingSettings);
    Assert.Single(viewModel.GlobalPolicies.Where(policy => policy.IsSelected));
}

[Fact]
public void SingleProjectSetupUsesProjectWordingButBuildsGlobalRequest()
{
    var viewModel = ScheduleSetupViewModelTestData.CreateSingleProjectWorkspace();
    var request = viewModel.BuildRequest();

    Assert.Equal("项目完成节奏", viewModel.PolicyLabel);
    Assert.Single(request.MatchGraphs);
    Assert.NotNull(request.Resources);
}
```

- [ ] **Step 2: Run and confirm missing setup/board failures**

```bash
dotnet test tests/BadmintonDraw.Desktop.Tests/BadmintonDraw.Desktop.Tests.csproj --filter FullyQualifiedName~ScheduleSetupPageViewModelTests --verbosity minimal
```

- [ ] **Step 3: Implement global resource setup**

The page manages dates, time windows, courts, unavailable court intervals, referee count, minimum rest, daily maximum, expected duration per project, and one policy. Generate calls the workspace workflow once and displays structured failure details without changing the persisted schedule.

- [ ] **Step 4: Port the v4 board as a reusable control**

Move rendering, zoom, day tabs, drag hover, auto-scroll, card lookup, and highlighting into `ScheduleBoardControl`. Keep state and command execution in `ScheduleBoardPageViewModel`; code-behind translates pointer events into `MoveMatchRequest` only.

- [ ] **Step 5: Port editing and undo tests**

Use `(ProjectId, MatchId)` identifiers. Test same-day move, cross-day move, blocked target, cascade move, undo, completed-match lock, stale revision, candidate rest, and restoration of the last legal schedule after failed custom regeneration.

- [ ] **Step 6: Run the board and full suites**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter FullyQualifiedName~ScheduleEditingWorkflowTests --verbosity minimal
dotnet test tests/BadmintonDraw.Desktop.Tests/BadmintonDraw.Desktop.Tests.csproj --verbosity minimal
dotnet test BadmintonDraw.sln -c Release --verbosity minimal
```

- [ ] **Step 7: Commit the global schedule UI**

```bash
git add src/BadmintonDraw.Desktop src/BadmintonDraw.Workflows/Tournaments tests
git commit -m "feat: add unified schedule setup and board"
```

---

### Task 10: Move results, materials, history, and recovery into the workspace

**Files:**
- Create: `src/BadmintonDraw.Workflows/Tournaments/ResultImportWorkflow.cs`
- Create: `src/BadmintonDraw.Workflows/Tournaments/OperationalPackageWorkflow.cs`
- Create: `src/BadmintonDraw.Desktop/ViewModels/OperationsPageViewModel.cs`
- Create: `src/BadmintonDraw.Desktop/Views/OperationsPage.axaml`
- Modify: `src/BadmintonDraw.Excel/MatchRecordReader.cs`
- Modify: `src/BadmintonDraw.Excel/ScheduleExcelWriter.cs`
- Modify: `src/BadmintonDraw.Excel/ScoreSheetExcelWriter.cs`
- Modify: `src/BadmintonDraw.Excel/DrawResultExcelWriter.cs`
- Create: `tests/BadmintonDraw.Tests/WorkspaceResultImportTests.cs`
- Create: `tests/BadmintonDraw.Tests/WorkspaceOperationalPackageTests.cs`
- Create: `tests/BadmintonDraw.Desktop.Tests/OperationsPageViewModelTests.cs`

**Interfaces:**
- Produces: project-qualified record import, cumulative result projection, per-project and merged packages, audit/history queries, and backup restoration UI.
- Consumes: `(ProjectId, MatchId)` match identity and one workspace store.

- [ ] **Step 1: Write failing project-qualified result tests**

```csharp
[Fact]
public void SameDisplayMatchNameInTwoProjectsDoesNotCollide()
{
    var workspace = WorkspaceResultTestData.CreateTwoProjectScheduleWithSameDisplayNames();
    var records = WorkspaceResultTestData.ResultsForBothProjects();

    var outcome = new ResultImportWorkflow().Apply(workspace, records, allowCorrections: false);

    Assert.Equal(2, outcome.Results.Count);
    Assert.Equal(2, outcome.Results.Select(result => (result.ProjectId, result.MatchId)).Distinct().Count());
}
```

- [ ] **Step 2: Run and confirm result identity failure**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter FullyQualifiedName~WorkspaceResultImportTests --verbosity minimal
```

- [ ] **Step 3: Add hidden workspace/project/match identifiers to record sheets**

Every exported record row carries `WorkspaceId`, `ProjectId`, and `MatchId`. Reader compatibility accepts only v5 identifiers. Preview reports duplicates by file SHA-256, conflicting winners, corrections, missing fields, and foreign workspace rows before mutation.

- [ ] **Step 4: Implement one-transaction result import**

All accepted rows, import logs, result history, processed days, audit events, stage transition, and downstream display resolution are written through one `Mutate` call. Any rejected row leaves the workspace file unchanged.

- [ ] **Step 5: Port operational exports**

Support per-project draw files, per-day project record sheets, merged daily schedule/record sheets, individual score PDFs, team score workbooks, conflict reports, and package descriptions. Export derives display rows from graph + placement + result projection, never from persisted `ScheduledMatch` snapshots.

- [ ] **Step 6: Add recovery and completion tests**

Cover duplicate import, correction confirmation, correction history, next-round resolution, backup before mutation, restore preserving workspace identity, corrupt backup rejection, stage `InProgress`, and transition to `Completed` after the final result.

- [ ] **Step 7: Run result/export/full gates**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter "FullyQualifiedName~WorkspaceResult|FullyQualifiedName~WorkspaceOperational" --verbosity minimal
dotnet test BadmintonDraw.sln -c Release --verbosity minimal
```

- [ ] **Step 8: Commit workspace operations**

```bash
git add src/BadmintonDraw.Workflows/Tournaments src/BadmintonDraw.Desktop src/BadmintonDraw.Excel tests
git commit -m "feat: integrate results and materials into v5 workspace"
```

---

### Task 11: Delete v4 internal paths and enforce a single v5 entry point

**Files:**
- Delete: `src/BadmintonDraw.Excel/TournamentProgressStore.cs`
- Delete: `src/BadmintonDraw.Excel/TournamentProgressModels.cs`
- Delete: `src/BadmintonDraw.Excel/TournamentProgressException.cs`
- Delete: `src/BadmintonDraw.Excel/ITournamentProgressStore.cs`
- Delete: `src/BadmintonDraw.Core/ScheduleDependencyBackfill.cs`
- Delete: `src/BadmintonDraw.Workflows/TournamentProgressWorkflow.cs`
- Delete: `src/BadmintonDraw.Workflows/CrossEventConflictWorkflow.cs`
- Delete: `src/BadmintonDraw.Workflows/CrossEventConflictWorkflow.ConflictAnalysis.cs`
- Delete: `src/BadmintonDraw.Workflows/CrossEventConflictWorkflow.Materials.cs`
- Delete: `src/BadmintonDraw.Workflows/CrossEventConflictWorkflow.Moves.cs`
- Delete: `src/BadmintonDraw.Workflows/CrossEventConflictWorkflow.Scheduling.cs`
- Delete: `src/BadmintonDraw.Desktop/MainWindow.axaml`
- Delete: `src/BadmintonDraw.Desktop/MainWindow.axaml.cs`
- Delete: all old `src/BadmintonDraw.Desktop/MainWindow.*.cs` partials
- Modify: `src/BadmintonDraw.Excel/BadmintonDraw.Excel.csproj`
- Modify: `src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj`
- Replace or delete: v4-specific tests after equivalent v5 tests are green
- Create: `tests/BadmintonDraw.Tests/V5ArchitectureBoundaryTests.cs`

**Interfaces:**
- Produces: one Desktop-to-Workflows entry point and clean project dependency boundaries.
- Consumes: all v5 services from Tasks 1–10.

- [ ] **Step 1: Write architecture boundary tests before deletion**

```csharp
[Fact]
public void ExcelAssemblyDoesNotReferenceSqlite()
{
    var references = typeof(ParticipantExcelReader).Assembly.GetReferencedAssemblies();
    Assert.DoesNotContain(references, reference => reference.Name == "Microsoft.Data.Sqlite");
}

[Fact]
public void V5AssembliesExposeNoLegacyProgressOrCrossEventWorkflowTypes()
{
    var names = typeof(TournamentWorkspaceWorkflow).Assembly.GetTypes().Select(type => type.FullName).ToList();
    Assert.DoesNotContain(names, name => name?.Contains("TournamentProgress", StringComparison.Ordinal) == true);
    Assert.DoesNotContain(names, name => name?.Contains("CrossEventConflictWorkflow", StringComparison.Ordinal) == true);
}
```

- [ ] **Step 2: Run and confirm both boundary tests fail**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter FullyQualifiedName~V5ArchitectureBoundaryTests --verbosity minimal
```

Expected: Excel still references SQLite and legacy workflow types still exist.

- [ ] **Step 3: Remove legacy classes and package references**

Move SQLite packages exclusively to Persistence. Delete v4 archive/backfill/multi-file save types and the complete old `MainWindow` surface. Remove their direct tests only after the v5 equivalent named in Tasks 4, 8, 9, and 10 passes.

- [ ] **Step 4: Search for forbidden entry points**

```bash
rg -n "TournamentProgress|CrossEventConflictWorkflow|ScheduleDependencyBackfill|加载多项目赛程|创建赛事存档" src tests tools
```

Expected: no production-code matches; any remaining matches are deliberate historical text in versioned documentation.

- [ ] **Step 5: Run boundary, vulnerability, build, and full tests**

```bash
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --filter FullyQualifiedName~V5ArchitectureBoundaryTests --verbosity minimal
scripts/check-vulnerable-packages.sh BadmintonDraw.sln
dotnet build BadmintonDraw.sln -c Release --no-restore --verbosity minimal
dotnet test BadmintonDraw.sln -c Release --no-build --verbosity minimal
```

- [ ] **Step 6: Commit legacy removal**

```bash
git add -A src tests tools
git commit -m "refactor: remove v4 archive and dual scheduling paths"
```

---

### Task 12: Build the v5 acceptance matrix and prepare 5.0.0

**Files:**
- Refactor: `tools/BadmintonDraw.Acceptance/Program.cs`
- Create: `tools/BadmintonDraw.Acceptance/V5AcceptanceScenario.cs`
- Create: `tools/BadmintonDraw.Acceptance/V5ArtifactValidator.cs`
- Create: `samples/v5/公开抽签_男单名单.xlsx`
- Create: `samples/v5/完整赛事_男单名单.xlsx`
- Create: `samples/v5/校长杯_男单名单.xlsx`
- Create: `samples/v5/校长杯_男双名单.xlsx`
- Create: `samples/v5/校长杯_混双名单.xlsx`
- Create: `samples/v5/完整团体赛名单.xlsx`
- Create: `docs/acceptance/v5.0.0.md`
- Modify: `README.md`
- Modify: `docs/usage.md`
- Modify: `docs/algorithm.md`
- Modify: `docs/scheduling.md`
- Modify: `docs/build.md`
- Modify: `Directory.Build.props`
- Modify: `scripts/publish-macos.sh`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Produces: repeatable v5 lifecycle acceptance JSON, final documentation, versioned Windows/macOS artifacts, and a release checklist.
- Consumes: the complete v5 workspace stack.

- [ ] **Step 1: Write acceptance assertions before updating the runner**

The runner must fail unless it observes all five scenarios from spec section 13, zero severe schedule issues, zero vulnerable packages, correct stage transitions, one `.szbd` per scenario, and a single three-project workspace file for the multi-project scenario.

```csharp
if (result.MultiProjectWorkspaceFileCount != 1 || result.MultiProjectProjectCount != 3)
{
    throw new InvalidOperationException("v5 multi-project acceptance requires one workspace containing three projects.");
}
```

- [ ] **Step 2: Run and confirm the old v4 acceptance runner fails the new contract**

```bash
dotnet run --project tools/BadmintonDraw.Acceptance/BadmintonDraw.Acceptance.csproj -c Release -- --output artifacts/acceptance/v5.0.0/red
```

Expected: failure because the runner still creates multiple v4 archives and lacks draw-only scenarios.

- [ ] **Step 3: Implement the five v5 scenarios**

Use deterministic seeds and shared student IDs across 男单、男双、混双. Exercise create → roster → explicit preview → export → confirm → close/reopen; then, for full tournaments, resources → one global schedule → materials → deterministic results → import → next-round refresh → backup restore → completion.

- [ ] **Step 4: Validate artifacts externally**

Recalculate every generated `.xlsx` copy with LibreOffice, scan formulas for errors, inspect data validation, run `pdfinfo`, verify embedded fonts/ToUnicode, render representative first/middle/last pages, and record counts and paths in acceptance JSON. Do not describe an unperformed Windows desktop or physical print check as passed.

- [ ] **Step 5: Complete the manual UI matrix**

On macOS and Windows, check wizard decisions, draw-only close/reopen, project-by-project public draw, single/multi strategy labels, schedule failure rollback, drag, undo, cascade, cross-day move, dark mode, scaling, dialogs, corrupt-file messaging, and the v4 rejection message. Record operator, OS, package hash, result, and screenshot path in `docs/acceptance/v5.0.0.md`.

- [ ] **Step 6: Set version and documentation to 5.0.0**

Set `VersionPrefix` to `5.0.0`. Rewrite README and user docs around workspace stages; remove instructions that tell users to create one archive per project or load multiple archives. Update release scripts and CI artifact names to `v5.0.0`.

- [ ] **Step 7: Run the final release gate**

```bash
dotnet restore BadmintonDraw.sln --locked-mode
scripts/check-vulnerable-packages.sh BadmintonDraw.sln
dotnet build BadmintonDraw.sln -c Release --no-restore --verbosity minimal
dotnet test BadmintonDraw.sln -c Release --no-build --verbosity minimal
dotnet run --project tools/BadmintonDraw.Acceptance/BadmintonDraw.Acceptance.csproj -c Release -- --output artifacts/acceptance/v5.0.0/final
dotnet publish src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
VERSION=5.0.0 bash scripts/publish-macos.sh osx-arm64
```

Expected: audit is clean, build has zero warnings/errors, all tests pass, every acceptance scenario succeeds, Windows PE is produced, macOS DMG verifies, and both artifacts contain version `5.0.0` plus the release commit.

- [ ] **Step 8: Commit release preparation**

```bash
git add Directory.Build.props README.md docs scripts .github tools samples src tests BadmintonDraw.sln
git commit -m "chore: prepare v5.0.0 unified workspace release"
```

- [ ] **Step 9: Push only after review**

After the user reviews the acceptance report and diff, push the feature branch, obtain successful Windows/macOS GitHub Actions evidence, then merge through the chosen branch-finishing workflow. Tag and publish `v5.0.0` only from the reviewed clean `main` commit.

---

## Milestone Review Gates

| Gate | Tasks | Review question | Required evidence |
| --- | --- | --- | --- |
| A | 1–2 | Is the v5 state machine correct before storage work? | .NET 10 CI, domain transition matrix |
| B | 3–4 | Can a draw-only or multi-project workspace round-trip atomically? | graph integrity, real SQLite failure tests |
| C | 5–7 | Does the product stop cleanly after public draw without scheduling? | workflow and ViewModel tests, exported draw package |
| D | 8–9 | Is there truly one scheduler and one board for one-to-five projects? | constraint suite, UI semantics tests, no project schedule |
| E | 10–11 | Is the complete live workflow on v5 and is all v4 internal code gone? | results/material tests, architecture boundary search |
| F | 12 | Is 5.0.0 ready to publish? | cross-platform CI, five E2E scenarios, artifact hashes, manual report |

Stop after each gate for code review. Do not begin the next gate with a failing full suite or an unreviewed persistence/schema change.
