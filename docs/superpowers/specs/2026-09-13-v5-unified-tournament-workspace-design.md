# v5.0 统一赛事工作区架构设计

## 1. 决策状态

- 状态：用户已于 2026-09-13 批准按计划实施；开发分支为 `feature/v5-unified-workspace`。
- 目标版本：`5.0.0`。
- 基线：`v4.6.0` / `4bc1c0f89f3ef3821d52e00947bb9bba2197af38`。
- 核心方案：一场赛事对应一个统一 `.szbd` 工作区，单项赛的一个或多个项目全部内嵌其中；单项目是项目数量为 1 的特例。
- 兼容策略：不读取、不迁移 4.x `.szbd`，不保留旧存档结构的回填代码。需要查看 4.x 存档时继续使用已发布的 v4.6.0。

## 2. 要解决的问题

v4.6 的用户流程按内部实现组织：先给每个项目抽签并生成一份局部赛程，再选择多个 `.szbd` 进入多项目工作台重新编排。这个流程有三个根本问题：

1. 用户必须填写最终会被全局排程覆盖的项目级“紧凑完成 / 均衡宽松 / 决赛日友好”参数。
2. 抽签与排程被过早串在一起，不适合深大羽协通过线上会议公开抽签、先导出和确认抽签结果、以后再排赛程的工作习惯。
3. 一场多项目赛事被拆成多个存档，带来跨文件保存、路径管理、备份恢复和审计一致性问题。

v5.0 将用户决策顺序改成赛事业务顺序，并把领域对象从“单项目进度存档”提升为“完整赛事工作区”。

## 3. 产品边界

### 3.1 必须支持

- 启动后选择“新建赛事工作区”或“打开赛事工作区”。
- 新建时先选择互斥的“团体赛”或“单项赛”。
- 团体赛工作区在 5.0 中只包含一个团体项目。
- 单项赛工作区包含 1 至 5 个不重复项目：男单、女单、男双、女双、混双。
- 新建时选择工作目标：“仅公开抽签”或“完整赛事筹备”。
- 名单导入后由用户主动开始抽签；程序不得因导入名单自动抽签或自动排程。
- 每个项目的抽签结果可独立预览、导出和确认。
- 仅公开抽签的工作区允许在“抽签已确认”阶段保存、关闭和重新打开。
- 仅公开抽签工作区可在以后显式升级为完整赛事筹备；升级不改变已确认抽签。
- 单项目赛程和多项目赛程都由同一个全局调度入口生成。
- 单项目时可以选择项目级语义的紧凑、均衡、决赛日友好或自定义策略。
- 多项目时只设置一次全赛事资源和全局策略，不生成任何项目级临时时间表。
- 赛果、导入日志、更正历史、材料导出记录和备份属于同一个工作区。

### 3.2 明确不支持

- 同一工作区同时包含团体赛和单项赛。
- 导入、迁移或原地升级 4.x `.szbd`。
- 云同步、多人实时协作、账号和权限系统。
- 自动安排具体裁判姓名、裁判资质或回避关系。
- 团体赛内部每轮上场阵容自动生成。
- 在 5.0 架构重构中增加新的抽签赛制、特殊名次签表或导出格式。

## 4. 用户流程

```text
启动页
├── 新建赛事工作区
│   ├── 团体赛
│   │   └── 仅公开抽签 / 完整赛事筹备
│   └── 单项赛
│       ├── 选择 1 个项目
│       ├── 选择 2–5 个项目
│       └── 仅公开抽签 / 完整赛事筹备
└── 打开赛事工作区
```

工作区内部使用阶段式导航：

```text
赛事信息
  → 各项目名单
  → 公开抽签
  → 全局赛程
  → 现场执行
  → 赛事完成
```

“仅公开抽签”在抽签确认和导出后完成本次操作，工作区仍停留在 `DrawsConfirmed`，同时保留“继续筹备完整赛事”入口。完整赛事只有在全部项目抽签已确认后才允许进入全局赛程。

## 5. 状态机

工作区状态固定为：

```text
Draft
  → RostersReady
  → DrawsConfirmed
  → ScheduleReady
  → InProgress
  → Completed
```

状态规则：

- `Draft`：赛事基本信息已创建，允许增删单项项目和导入名单。
- `RostersReady`：所有项目名单通过校验，允许开始公开抽签。
- `DrawsConfirmed`：所有项目抽签已确认；允许导出正式抽签材料。仅抽签工作区可停在此状态。
- `ScheduleReady`：已生成完整且无严重冲突的全局赛程。
- `InProgress`：至少一场赛果已写入；已完成场次和所有上游抽签被冻结。
- `Completed`：所有需要完成的场次都有有效赛果。

可逆操作仅限：

- 在没有确认抽签时修改名单或项目配置。
- 在没有赛果时，用户显式选择“解除抽签确认并作废下游赛程”；该操作清除受影响项目的比赛关系图和整个全局赛程，并写入审计事件。
- 在没有赛果时重新生成全局赛程；已有赛果后只能移动未完成场次。
- `PublicDrawOnly` 可升级为 `FullTournament`；生成赛程后不能降级。

## 6. 领域模型

### 6.1 聚合根

`TournamentWorkspace` 是唯一聚合根：

```csharp
public sealed record TournamentWorkspace(
    Guid Id,
    string Name,
    TournamentKind Kind,
    TournamentPurpose Purpose,
    TournamentStage Stage,
    IReadOnlyList<TournamentProject> Projects,
    TournamentResourcePlan? Resources,
    TournamentSchedule? Schedule,
    IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> Results,
    IReadOnlyList<WorkspaceAuditEvent> AuditEvents,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Revision);
```

`Revision` 每次成功写入加一，用于检测同一工作区被两个窗口先后打开后发生的陈旧覆盖。

### 6.2 项目

```csharp
public sealed record TournamentProject(
    Guid Id,
    EventDiscipline Discipline,
    string DisplayName,
    CompetitionMode CompetitionMode,
    ProjectRoster? Roster,
    ProjectDraw? Draw,
    MatchGraph? MatchGraph,
    int SortOrder);
```

`EventDiscipline` 固定为 `MenSingles`、`WomenSingles`、`MenDoubles`、`WomenDoubles`、`MixedDoubles`、`Team`。同一单项赛工作区不能出现重复项目；团体赛工作区必须且只能包含一个 `Team` 项目。

### 6.3 比赛结构与时间安排分离

领域数据流固定为：

```text
ProjectRoster
  → ProjectDraw
  → MatchGraph
  → TournamentSchedule
  → TournamentMatchResult
```

`MatchGraph` 只描述比赛身份、阶段、组别、两侧来源和胜负去向，不允许包含日期、时间或场地。参赛方来源使用显式联合类型：固定参赛者、某场胜者、某场负者或轮空。

`TournamentSchedule` 只保存全赛事资源设置和 `MatchId → MatchPlacement`。`MatchPlacement` 保存比赛日、开始时间、结束时间和场地。界面及导出通过投影服务把 `MatchGraph`、`MatchPlacement` 和当前赛果组合为可显示比赛。

这样可以保证抽签确认后先公开比赛结构，而不要求提前生成时间表；多项目也不再产生随后会被覆盖的局部赛程。

## 7. 调度语义

对外只有一个调度接口：

```csharp
TournamentSchedulingResult Generate(
    TournamentWorkspace workspace,
    TournamentResourcePlan resources,
    TournamentSchedulingPolicy policy);
```

- 项目数为 1 时，界面把策略解释为该项目的完成节奏。
- 项目数大于 1 时，策略作用于整场赛事；每日负载率、阶段推进、决赛日目标和最小休息统一设置。
- 每个项目仍可设置预计单场时长和赛制参数，但不能设置独立的日期、场地或自动编排策略。
- 调度硬约束继续包括依赖顺序、场地占用、不可用时段、裁判并发、确定选手撞场、候选晋级路径休息间隔和每日场次上限。
- 软评分继续包括紧凑度、每日负载均衡、阶段波次、决赛日目标、少跨日和少移动。
- 调度失败返回结构化 `SchedulingFailure`，包含未安排场次、失败约束和建议调整的资源，不得写入半成品赛程。

手工拖动、撤销、连锁移动和跨日移动均操作同一个 `TournamentSchedule`，不再区分单项目板与多项目板的底层模型。

## 8. 工程分层

```text
BadmintonDraw.Core
  纯领域模型、抽签算法、比赛关系图、统一调度器和约束分析

BadmintonDraw.Persistence
  v5 SQLite 工作区、事务、候选副本、备份、恢复和完整性校验

BadmintonDraw.Excel
  名单导入、Excel/PDF/图片导出；不再包含 SQLite 存档逻辑

BadmintonDraw.Workflows
  应用命令、查询、状态转换、自动保存和导出编排

BadmintonDraw.Desktop
  Avalonia Shell、页面、ViewModel、文件选择和拖拽交互
```

项目依赖固定为：

```text
Persistence → Core
Excel → Core
Workflows → Core + Persistence + Excel
Desktop → Core + Workflows
```

不新增外部 MVVM 框架。Desktop 内提供轻量 `ViewModelBase`、`DelegateCommand`、`AsyncCommand` 和 `WorkspaceNavigator`，避免架构迁移同时引入新的 UI 状态库。

## 9. v5 存档

继续使用 `.szbd` 扩展名，但采用全新 SQLite `user_version = 500`。打开其他版本时给出明确提示：该文件不是 v5 工作区，请使用 v4.6.0 打开；不提供迁移按钮。

数据库表：

- `workspace`：名称、类型、目标、阶段、版本、修订号和时间戳。
- `projects`：项目身份、类型、赛制、显示名和顺序。
- `project_rosters`：名单 JSON、导入警告、来源文件名和内容哈希。
- `project_draws`：抽签设置、抽签结果、审计信息和确认时间。
- `match_graphs`：每个项目的比赛关系图 JSON 和图版本。
- `resource_plan`：全赛事比赛日、场地、不可用时段、裁判容量和负荷限制。
- `schedule`：调度策略、图版本集合、排程修订号和位置 JSON。
- `match_results`：以 `(project_id, match_id)` 为主键的当前赛果。
- `processed_days`：已处理比赛日。
- `import_logs`：导入文件、哈希、数量和警告。
- `result_history`：赛果更正前后值及原因。
- `audit_events`：名单替换、抽签预览、抽签确认、解除确认、策略重排、手工移动、导出和恢复事件。

所有修改使用同一套写入算法：

1. 检查期望 `Revision`。
2. 复制正式文件为同目录候选文件。
3. 在候选文件的单个 SQLite 事务中执行命令。
4. 执行 `PRAGMA integrity_check` 和领域不变量校验。
5. 备份正式文件。
6. 原子替换正式文件。
7. 重新打开并返回新工作区快照。

一场赛事只有一个文件，因此 v4.6 的多存档补偿事务在 5.0 中被删除。

## 10. 应用命令

`TournamentWorkspaceWorkflow` 作为 Desktop 的单一入口，公开下列命令：

```csharp
CreateWorkspace(CreateWorkspaceRequest request)
OpenWorkspace(string path)
ImportRoster(Guid projectId, string inputPath, long expectedRevision)
PreviewDraw(Guid projectId, DrawSettings settings, long expectedRevision)
ConfirmDraw(Guid projectId, long expectedRevision)
ReopenDraw(Guid projectId, string reason, long expectedRevision)
UpgradeToFullTournament(long expectedRevision)
GenerateSchedule(TournamentResourcePlan resources, TournamentSchedulingPolicy policy, long expectedRevision)
MoveMatch(MoveMatchRequest request, long expectedRevision)
UndoLastScheduleEdit(long expectedRevision)
PreviewResultImport(IReadOnlyList<string> paths)
ImportResults(IReadOnlyList<string> paths, bool allowCorrections, long expectedRevision)
ExportDrawPackage(Guid? projectId, DrawExportRequest request)
ExportOperationalPackage(OperationalExportRequest request)
RestoreBackup(string backupPath)
```

命令成功即自动保存。查询返回不可变快照；Desktop 不直接持有 SQLite 连接，也不直接拼接跨项目状态。

## 11. Desktop 导航

`AppShellWindow` 只保留窗口、标题栏、全局状态条和当前页面宿主。迁移期先与旧 `MainWindow` 并存，待 v5 页面通过验收后再删除旧窗口。页面划分为：

- `StartPage`：新建、打开、最近工作区。
- `NewWorkspaceWizardPage`：赛事类型、工作目标、项目、名称和保存位置。
- `WorkspaceOverviewPage`：显示阶段、各项目完成度和下一步操作。
- `RostersPage`：按项目导入、验证和确认名单。
- `PublicDrawPage`：逐项目公开抽签、展示随机种子与审计信息、导出并确认。
- `ScheduleSetupPage`：统一设置比赛日、场地、裁判、最短休息和全局策略。
- `ScheduleBoardPage`：统一时间场地表、提醒、拖动、撤销和连锁移动。
- `OperationsPage`：材料包、记录表导入、赛果更正、备份和完成状态。

页面导航由 `TournamentStage` 和 `TournamentPurpose` 决定。用户可以回看已完成阶段，但被状态机冻结的控件只读显示，不能靠隐藏按钮绕过领域规则。

## 12. 错误处理

- 领域校验错误返回面向用户的中文说明和稳定错误码。
- 调度失败显示未安排场次、触发约束和可调整参数；保留上一份合法赛程。
- 写入失败保持正式文件不变，并显示候选文件是否删除、备份路径和原始错误。
- 修订冲突提示工作区已被其他窗口更新，要求重新载入；不自动覆盖。
- 导出失败不改变工作区业务状态，但写入失败审计仅保存在应用日志，不污染正式存档。
- 选择 4.x 存档时只给出版本不兼容说明和 v4.6.0 Release 地址，不执行推测性读取。

## 13. 测试与验收

自动化测试至少覆盖：

- 团体/单项互斥、单项项目唯一性和所有状态转换。
- 抽签前不产生 MatchGraph，确认后产生稳定 MatchGraph。
- MatchGraph 不含时间场地；修改名单或重抽会按规则失效下游数据。
- 仅抽签工作区保存、关闭、重开、导出和升级到完整赛事。
- 单项目与多项目调用同一调度器，多项目不会生成项目级 Schedule。
- 统一存档的候选写入、事务失败、语义校验失败、修订冲突、损坏文件和备份恢复。
- `(project_id, match_id)` 赛果定位、重复导入、冲突胜方、更正历史和后续对阵刷新。
- 单/多项目统一赛程板的拖动、撤销、连锁、跨日和已完成场次锁定。
- Excel 公式、PDF 中文字体与分页、团体记分表和合并材料包。

端到端场景固定为：

1. 仅公开抽签的单项目单项赛。
2. 完整单项目单项赛。
3. 男单、男双、混双三项目完整赛事，包含兼项选手。
4. 完整团体赛。
5. 故障注入：候选写入失败、原子替换失败、损坏存档和陈旧修订保存。

## 14. 技术基线

- 目标框架：`.NET 10`，`global.json` 从本机已安装的 `10.0.301` 起锁定并允许同 feature band 的安全补丁前滚。
- Avalonia：保留 12.x 主线；第一阶段不同时升级 Avalonia 大版本。
- SQLite、ClosedXML、SkiaSharp：先保留当前已验证版本，只在漏洞审计或 `.NET 10` 兼容性要求时做最小升级。
- CI：Windows 与 macOS 均执行锁定还原、漏洞审计、Release 构建、全量测试和发布冒烟。

选择 .NET 10 的原因是 [.NET 8 将于 2026-11-10 结束支持，而 .NET 10 LTS 支持到 2028-11-14](https://dotnet.microsoft.com/en-us/platform/support/policy)；[Avalonia 12 官方也将 .NET 10 列为推荐目标](https://docs.avaloniaui.net/docs/avalonia12-breaking-changes)。

## 15. 实施顺序

v5.0 拆成九个可独立评审的里程碑：

1. .NET 10 与领域状态机。
2. 比赛关系图和“结构/位置”拆分。
3. 单文件 v5 Persistence。
4. 工作区命令与自动保存。
5. Avalonia Shell、新建向导和阶段导航。
6. 名单与公开抽签工作流。
7. 统一单/多项目调度和赛程板。
8. 现场赛果、材料、审计与恢复。
9. 删除 v4 内部路径、端到端验收和 5.0.0 发布。

每个里程碑必须在主干保持可构建、可测试；旧界面可以在迁移期间临时存在，但在里程碑 9 前必须删除，正式 5.0 不保留“双入口”。
