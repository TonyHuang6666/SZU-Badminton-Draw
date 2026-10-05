# 系统架构

一场赛事由一个 `.szbd` 文件承载。赛事内的项目共享日期、场地、裁判容量和全局赛程；名单、确认签表和比赛关系图按项目保存；现场赛果与计划位置分别保存。本文说明当前源码的模块边界、数据流和保存协议。领域细节见[算法与工作区模型](algorithm.md)，排程行为见[赛程编排](scheduling.md)，开发命令见[构建说明](build.md)。

## 解决方案与依赖

| 项目 | 主要职责 | 直接项目依赖 |
| --- | --- | --- |
| `BadmintonDraw.Core` | 领域模型、名单身份、规则化抽签、类型化比赛图、全局调度、硬约束及质量分析 | 无 |
| `BadmintonDraw.Persistence` | SQLite schema、序列化、修订校验、候选文件、备份与恢复 | Core |
| `BadmintonDraw.Excel` | 名单与记录表读取、工作簿生成、PDF/图片渲染、字体与记分表模板 | Core |
| `BadmintonDraw.Workflows` | 应用命令、会话、阶段门禁、导入检查、导出发布和审计 | Core、Persistence、Excel |
| `BadmintonDraw.Desktop` | Avalonia 窗口、导航、ViewModel、文件选择、确认与反馈 | Core、Workflows |
| `BadmintonDraw.Tests` | 领域、存储、工作流、材料格式和故障回归测试 | 相关生产类库 |
| `BadmintonDraw.Desktop.Tests` | Avalonia Headless 控件、绑定、页面生命周期和异步回调测试 | Desktop |
| `BadmintonDraw.Acceptance` | 真实工作流生命周期验收、规模场景、排程审计及证据目录 | 相关生产类库 |

生产代码的 SQLite 包引用集中在 Persistence；Excel 使用 ClosedXML 处理工作簿、SkiaSharp 渲染材料，并内嵌 Noto Sans SC 字体与个人赛记分表模板。Desktop 通过 `TournamentWorkspaceWorkflow` 执行赛事命令，文件选择器、窗口和线程切换由桌面层管理。

顶部帮助窗口在构建时嵌入当前使用文档、技术文档和发布说明，运行时通过 Markdig 解析 Markdown，并用 Avalonia 原生控件显示。文档跳转限定在内置目录内，外部网页仅响应用户点击；阅读功能可离线使用。模板导出窗口独立于赛事会话，直接复用 Excel 层的空白名单和计分表布局，经系统文件夹选择与同名文件确认后输出，不创建赛事数据或导出审计记录。

源码入口：[`src/`](../src/)、[`TournamentWorkspaceWorkflow`](../src/BadmintonDraw.Workflows/Tournaments/TournamentWorkspaceWorkflow.cs)、[`V5ArchitectureBoundaryTests`](../tests/BadmintonDraw.Tests/V5ArchitectureBoundaryTests.cs)。

## 领域状态与身份

`TournamentWorkspace` 是经过校验的候选快照；集合在构造和赋值时复制为只读快照。反序列化、工作流提交和存储重读都会执行领域校验。

```text
TournamentWorkspace
├── Id / Name / Kind / Purpose / Stage / Revision
├── TournamentProject[]
│   ├── 项目身份、项目类型、赛制和顺序
│   ├── ProjectRoster：名单、身份、种子、来源哈希
│   ├── ProjectDraw：抽签结果、审计、确认时间
│   └── MatchGraph：无时间的比赛节点与依赖
├── TournamentResourcePlan：全赛事日期、场地、容量和休息设置
├── TournamentSchedule：位置、策略、图版本和赛程修订
├── Results：WorkspaceMatchKey → TournamentMatchResult
└── ProcessedDays / ImportLogs / ResultHistory / AuditEvents
```

几个身份层次承担不同职责：

| 身份或版本 | 用途 |
| --- | --- |
| `Workspace.Id`、`Project.Id` | 识别赛事与所属项目 |
| `MatchGraph` 节点 `Id` | 标识实际比赛；`TournamentSchedule.Placements` 以该 GUID 为键 |
| `WorkspaceMatchKey(ProjectId, MatchId)` | 限定赛果、记录表和界面操作的项目与场次 |
| `MatchGraph.Revision`、抽签确认时间 | 核对材料对应的确认签表 |
| `Workspace.Revision` | 每次持久化更新递增，防止陈旧命令覆盖现有存档 |
| `TournamentSchedule.Revision` | 标识位置版本；生成与编辑均取工作区/赛程修订的更大值加一 |

比赛双方使用 `EntrantSource.Participant`、`WinnerOf`、`LoserOf`、`Bye` 表达。后续对阵通过类型化依赖和已保存赛果解析。Excel 中的姓名、显示标签和公式属于材料呈现；导入依据隐藏的赛事、项目、比赛和图版本标识核对来源。

`TournamentStage` 包含 `Draft`、`RostersReady`、`DrawsConfirmed`、`ScheduleReady`、`InProgress`、`Completed`。阶段由业务命令按实际内容维护。界面“完成与归档”是导航页面，打开该页面本身不会将赛事写成 `Completed`。仅公开抽签赛事可以在确认抽签后查看归档入口，也可以通过显式命令升级为完整赛事。

## 工作流与会话

`TournamentWorkspaceWorkflow` 提供同步命令，内部通过 `sessionGate` 串行处理同一实例的命令。命令在等待门禁前捕获 `WorkspaceSession`，取得门禁后再次核对对象身份，防止排队操作落到后来打开的赛事上。涉及文件的命令还核对正式存档的赛事身份、修订和相应内容指纹。

保存成功后发布一个新会话，并触发 `SessionChanged`。事件在命令线程触发，桌面订阅方负责调度到 UI 线程；通知回调中的命令重入会被拒绝。订阅者异常由工作流记录，已完成的保存继续保持已提交状态。

`WorkspaceCommandResult` 携带实际保存快照、路径、备份路径和提示；`WorkspaceError` 保留错误码、候选路径、备份路径、`Committed` 标记及可选排程诊断。提交后重读失败时，工作流尝试刷新会话；仍无法读取则设置 `RequiresReload`，后续修改需要重新打开文件。

## 名单、抽签与确认

```text
名单 XLSX → 捕获字节与 SHA-256 → 按项目解析、身份校验 → 保存名单
  → 用户开始公开抽签或重新抽签 → 生成并保存待确认结果
  → 用户确认 → 保存确认时间与 MatchGraph
```

全部项目名单通过校验后，才可开始抽签。导入名单、生成抽签、确认抽签和导出材料分别由独立命令发起；导出待确认结果保持其待确认状态。抽签审计保存随机种子与输入信息，以便复核生成条件。

桌面显示“抽签结果（待确认）”和“抽签结果（已确认）”；文件名相应包含“待确认抽签结果”或“已确认抽签结果”。源码中的 `PreviewDraw`、相关枚举和 `DrawPreviewed` 审计动作表示生成并保存待确认结果。解除确认需要理由，并按领域规则处理依赖于原签表的状态。

## 全局赛程生成与手工调整

生成入口要求赛事用途为完整赛事、所有项目均已确认抽签且具备比赛图、当前赛果数量为零，阶段为 `DrawsConfirmed` 或 `ScheduleReady`。已有赛程可作为软评分参考位置，日期、场地和策略由本次请求提供。

```text
全部已确认 MatchGraph + 全赛事资源/策略 + 可选原赛程位置
  → 构建候选身份与依赖上下文
  → 输入、已锁定位置与容量预检查
  → 有界搜索、完整硬约束校验
  → 可选优化与质量分析
  → 再次核对来源 → 一次保存完整赛程及审计
```

`TournamentScheduler` 对上下文、预检查、搜索、校验、优化和质量分析分别计量工作预算，并支持取消。完整搜索候选通过硬约束后，才可进入成功结果；可选优化未能完成最终校验时保留已验证的完整候选。质量分析未完成的范围随结果报告。预算单位属于算法工作量，耗时仍受输入和机器影响。

失败诊断区分无效输入、已有容量证据的不可行、搜索未完成、校验未完成和取消。搜索或校验预算耗尽只能说明本次运行未完成对应工作。生成失败在进入存储修改前返回，原赛程和存档保持原状态。单项目也使用同一套全赛事调度与校验。

手工移动、连锁移动和撤销由 [`ScheduleEditingWorkflow`](../src/BadmintonDraw.Workflows/Tournaments/ScheduleEditingWorkflow.cs) 处理。编辑基线绑定会话、来源内容和赛程版本；候选位置以全部项目、当前赛果、资源和完整赛程复验。连锁移动调整本项目的后续依赖，同时接受全赛事约束检查。已有赛果的场次受位置锁定约束。撤销记录仅存在于会话内，撤销时再次检查当前状态，并保留其间新增的赛果、导入历史和审计。

## 现场记录导入

导入分为只读检查与明确提交两个阶段：

1. `PreviewResultImport` 捕获所选路径，每个文件读取一次字节，并由同一字节副本完成哈希与解析。
2. 读取并核对正式工作区，检查整批来源标识、比赛依赖、赛果、重复记录和更正，生成检查结果与待确认事项。
3. 用户确认后，`ImportResults` 核对检查结果所属工作流和会话、预期修订、来源内容；重新捕获所选文件，并逐个比较检查时的 SHA-256。
4. 根据本次确认重新评估，核对更正内容一致性；通过后一次保存赛果、凭据、覆盖记录、更正历史和审计。

文件在检查后变化、来源会话变化或存在阻断问题时，需要重新检查。内容没有变化的导入直接返回 `NoChanges`，保持修订和会话。实际比赛日保存在赛果中，计划位置保存在赛程中；导入完成后，下一批材料由用户另行导出。

材料身份与公式细节由 [`WorkspaceMatchRecordReader`](../src/BadmintonDraw.Excel/WorkspaceMatchRecordReader.cs) 和工作流校验共同处理。办公软件公式重算、人工视觉检查的验证方法见[构建说明](build.md)。

## SQLite 保存、备份与恢复

`.szbd` 的 `PRAGMA user_version` 为 **500**，日志模式为 `DELETE`。schema 按赛事、项目、名单、抽签、比赛图、资源、赛程、赛果、比赛日处理记录、导入日志、更正历史和审计分表；各表使用身份、外键、唯一约束及 JSON 载荷保存对象。全赛事资源只存一份。

读取必须满足当前 schema 版本、精确的数据表集合、SQLite `integrity_check`、`foreign_key_check` 及重建后的领域校验。版本不匹配会返回 `UnsupportedWorkspaceVersion`，读取入口要求 schema 500。

正常修改由 [`TournamentWorkspaceStore`](../src/BadmintonDraw.Persistence/TournamentWorkspaceStore.cs) 按以下顺序执行：

1. 取得进程内路径锁和同路径 `.szbd.lock` 文件的独占写租约，再读取当前修订。
2. 将正式文件复制为同目录候选文件，在候选 SQLite 事务中生成并写入新状态。
3. 提交候选事务，执行领域校验，再完整读取候选文件进行复验。
4. 为当前正式文件创建同目录备份。
5. 发布候选文件替换正式目标，重新读取后返回新快照。

租约最长等待约 10 秒，超时返回 `WorkspaceBusy`；旁路锁文件持久保留，避免等待进程之间的删除重建竞争。候选和备份名称分别为 `.<赛事文件名>.<GUID>.candidate.szbd`、`.<赛事文件名>.<GUID>.backup.szbd`，当前实现保留这些副本，没有按数量自动清理的策略。

错误结果区分替换前失败和 `CommittedReadFailed`。前者正式目标保持原状态，后者目标已保存但重读失败。候选、备份及 `Committed` 信息应一路保留到界面和诊断记录；文件已存在本身不能证明失败副本通过完整性校验。

正常回退要求当前赛事可读、修订匹配，备份经过完整读取且哈希/赛事身份与确认时一致。损坏文件恢复还核对目标前像哈希，保留被替换文件，并创建新的单调修订与恢复审计。恢复提交前会再次验证备份和前像，恢复完成后旧检查结果需要重新取得。

## 材料发布与覆盖确认

`DrawPackageWorkflow` 规划抽签材料，`OperationalPackageWorkflow` 规划指定项目和日期范围的现场材料。规划后在暂存目录生成文件、校验材料、逐项发布，再通过工作区保存记录审计。导出审计记录来源修订和已生成材料信息。

多个输出文件与 `.szbd` 属于独立文件，发布过程可能发生部分成功。错误返回应保留已验证输出、当前尝试发布的路径、候选/备份信息以及仍存在的暂存目录；操作人员据此判断材料与存档审计的实际状态。暂存目录清理失败也会报告。

桌面在导出前调用只读冲突检查，复用实际规划，列出本次范围中已存在的确切目标。存在冲突时，用户明确确认本次覆盖路径。桌面请求的 `ConfirmedOverwritePaths` 始终非空值：无冲突时为空集合，有冲突时为明确接受的路径。实际发布再次检查目标；后来新增的冲突需要新的确认。

底层 `OverwriteExisting` 仅在调用方未提供路径集合时作为覆盖策略。赛事存档、备份、与原名单同名的文件、符号链接和文件夹受到目标保护。该机制校验路径与发布时状态，对外部进程在检查间隙替换目录或已确认文件的行为没有文件身份锁定保证。

## 桌面呈现与事件所有权

`App` 创建 `AppShellWindow`；Shell 持有工作流会话、页面工厂、导航、忙碌状态、恢复入口和独立窗口所用的 ViewModel。六个主步骤为“比赛概览、准备名单、公开抽签、安排赛程、比赛现场、完成与归档”。赛程板拥有独立路由与窗口，归属于安排赛程流程。

耗时命令通过 `Task.Run` 离开 UI 线程。`SessionChanged` 经注入的 UI 调度器回到界面，应用结果前再次比较当前会话。只读查询也检查开始和完成时的会话；导出设置、文件选择、覆盖确认、赛程悬停检查和导入检查使用来源会话及输入代次拒绝迟到结果。

窗口与模型的生命周期分工如下：

- Shell 拥有赛程板和兼项明细模型；相同赛事的新会话刷新现有模型，切换赛事或相关路由失效时释放模型并关闭窗口。
- 独立窗口关闭时清理自身绑定与引用，Shell 可在当前赛事中再次使用仍有效的模型。
- 赛程板视图在附加或更换 `DataContext` 时绑定焦点与悬停回调，在脱离视觉树或窗口关闭时解除绑定。
- 主窗口关闭时解除窗口事件订阅，Shell 的 `Dispose` 解除工作流事件并释放所持页面模型。

`AppShellViewModel.Presentation` 负责标题、阶段、保存状态、下一步提示和明暗主题。原始错误码、候选路径和备份路径通过操作详情呈现；首页与新建向导显示各自的上下文。公开抽签页通过一个导出设置窗口选择项目范围、格式和 PDF 分页；范围资格由当前已生成结果和确认状态决定，取消窗口保持已接受设置与赛事状态。

最近赛事列表独立保存在本机应用数据目录的 `SZU-Badminton-Draw/recent-workspaces.json`，最多 10 条。条目记录绝对路径、上次打开时间和已知的工作区保存时间，按最近活动排序；缺少时间的条目显示“时间未记录”。首页卡片显示赛事文件名与最近活动时间，文件缺失时显示不可用提示；悬停可查看完整路径和详细时间。移除条目只修改本机列表。赛事领域状态仍由打开后的 `.szbd` 会话提供。

## 扩展与验证边界

新增领域约束放入 Core，并让自动生成、手工编辑、导入和持久化入口共享适用校验。存储字段改动需同时检查 schema、序列化、真实文件重开与恢复。材料格式改动由 Excel 实现读写，Workflows 负责范围、确认、发布结果和审计，Desktop 负责呈现和收集输入。

持续回归应覆盖来源身份、预期修订、重复导入、保存前后故障、部分发布、恢复、取消以及迟到回调。Headless 和托管材料读回的验证范围有限；原生文件选择器、Office 重算、PDF 逐页外观和物理打印需按[构建与验收流程](build.md)分别记录证据。
