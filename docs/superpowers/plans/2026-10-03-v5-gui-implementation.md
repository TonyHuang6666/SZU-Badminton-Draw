# v5 赛事助手 GUI Implementation Plan

> **For agentic workers:** Use the approved design and execute the owned tasks below; keep changes inside the named files. Track verification here.

**Goal:** Deliver a polished, approachable Avalonia desktop interface for ordinary badminton organizers.

**Architecture:** Retain the existing workflow commands and data protection. Rebuild presentation around guided stages, contextual next actions and progressive disclosure. Shared styles provide consistent light/dark surfaces, primary buttons, readable forms and navigation states.

**Tech Stack:** .NET 10, Avalonia 12, existing ViewModels and Headless tests.

**Spec:** `docs/superpowers/specs/2026-09-20-v5-beginner-friendly-gui-design.md`

## Global Constraints

- One tournament contains team OR individual disciplines; never both.
- Import, draw preview, confirmation, export and scheduling remain explicit separate actions.
- Preserve storage, algorithm, result validation, backup and rollback semantics.
- No new runtime dependency. Support existing light/dark themes and keyboard interaction.
- Use existing isolated `feature/v5-unified-workspace` worktree; keep work local for review.
- User authorization from the approved design and 2026-10-03 implementation request covers execution; do not repeat approval gates.

## Review Focus

- New tournament opened over an existing session must hide the previous tournament header/sidebar/path.
- Public-draw-only users must be able to stop after confirmation/export without scheduling.
- Advanced settings must remain reachable; hiding controls must not clear their values.
- Workspace changes must still invalidate stale confirmations and asynchronous results.
- Compact windows and both themes must keep actions and explanatory text readable.

## Shared presentation contract

Parent owns `App.axaml`, `Styles/AssistantStyles.axaml`, shell, start, overview, navigation and archive.
Child tasks may use existing `App*Brush` resources plus these classes:
`page-title`, `page-subtitle`, `eyebrow`, `section-title`, `muted`, `card`, `inset`, `callout`, `primary`, `secondary`, `quiet`, `danger`, `action-bar`.
Primary action is filled teal; secondary outlined; quiet has minimal chrome. Cards have 18 px radius, 24 px padding. Pages use 28–32 px margins, 20–24 px section spacing and fixed action areas where feasible.

## Task 1: Shell, design system, welcome and progress (parent)

Files: `App.axaml`, `Styles/AssistantStyles.axaml`, `AppShellWindow.axaml`, `AppShellViewModel.cs`, `StartPageViewModel.cs`, `StartPage.axaml`, `WorkspaceOverviewPage*`, `Navigation/*`, new archive page.

- [x] Build shared theme styles and a calm, distinct visual hierarchy.
- [x] Add numbered current/completed/blocked navigation with accessible hints. Keep schedule board reachable within scheduling and add completion/archive route.
- [x] Hide old session context on start/new; display concise status with expandable technical details.
- [x] Rebuild welcome, recent tournaments, overview and final archive actions using real session data.
- [x] Add regression assertions for welcome context, stage navigation and archive eligibility; run relevant Headless tests.

## Task 2: Wizard, roster and public draw (agent)

Files: `Views/NewWorkspaceWizardPage.axaml`, `Views/RostersPage.axaml`, `Views/PublicDrawPage.axaml`, their page/project ViewModels and owned tests.

- [x] Present tournament type/purpose as explanatory selection cards and show active wizard progress.
- [x] Present roster readiness with plain-language guidance, clear template/import actions and folded technical evidence.
- [x] Separate draw preparation, preview/confirmation and export visually; preserve explicit confirmation and draw-only stopping point.
- [x] Run owned tests; verify binding compilation after integration.

## Task 3: Scheduling and board (agent)

Files: `Views/ScheduleSetupPage.axaml`, `Views/ScheduleBoardPage.axaml`, `Controls/ScheduleBoardControl.axaml`, scheduling page/field ViewModels and owned tests.

- [x] Show daily times/courts and project duration first. Fold targets, referee windows, split timing and final-round preferences into advanced sections.
- [x] Explain single-project versus global strategy and preserve all values/validation.
- [x] Make board viewing dominant, with clearly labeled collapsible movement controls and visible preview feedback.
- [x] Run owned tests for draft preservation, movement/undo and real control layout.

## Task 4: Operations and recovery (agent)

Files: `Views/OperationsPage.axaml`, `Views/ResultImportPanel.axaml`, `Views/WorkspaceRecoveryPanel.axaml`, owned operation/recovery ViewModels and tests.

- [x] Put material export before result import in visual task order; keep tab indexes compatible or update coherent tests.
- [x] Fold low-frequency details while preserving scope confirmation, diagnostics, correction evidence and partial-save outcomes.
- [x] Make backup/recovery steps readable and clearly distinguish normal and damaged-file recovery.
- [x] Run owned tests for composition, confirmation and asynchronous lifecycle.

## Task 5: Integrated verification, review and handoff (parent)

- [x] `dotnet build BadmintonDraw.sln -c Release --no-restore` succeeds.
- [x] `dotnet test BadmintonDraw.sln -c Release --no-build --no-restore --verbosity minimal` passes.
- [x] Inspect actual native screenshots of welcome/wizard and representative tournament pages in both themes; verify compact 960×680 shell geometry with actual Headless controls. Full native compact/platform matrix remains outside this local check.
- [x] Review changes for broken navigation, hidden critical diagnostics and stale state; fix actionable findings.
- [x] Update current user documentation and report concrete verification/remaining platform limits.

## Verification record

See [GUI local verification](../../acceptance/v5-gui-2026-10-03.md): 1,105 tests (779 shared, 326 Desktop), zero-warning Release build, representative macOS native review and local preview packaging. Independent review findings were addressed, including stale errors, read-only disclosure, zero-height compact board and overlay-obscured locate actions. No commit, push or official release is part of this implementation handoff.
