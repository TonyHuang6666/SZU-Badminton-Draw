# v5.0 Global Scheduling Reliability Implementation Plan

> 资料归档：本文保存文中日期、提交与验证范围对应的记录。当前程序的功能和操作见[文档中心](../../../index.md)。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 让 5.0 更早解释确实矛盾的排程条件，并通过有界回溯与跨日均衡找到更多完整合法方案，不以放松规则换取成功。

**Architecture:** 保留当前比赛图、资源、策略和唯一硬约束口径。请求级预算贯穿上下文构建、容量证明、可撤销搜索、最终验证及质量分析；只有完整验证的赛程才进入既有修订号保护的保存流程。

**Tech Stack:** C# / .NET SDK 10.0.301（遵循 `global.json`）、Avalonia 12.0.4、xUnit、现有 Excel 与 SQLite 工程；不新增产品依赖。

**Spec:** [已确认设计](../specs/2026-10-03-v5-global-scheduling-reliability-design.md)

**实施状态（2026-10-04）：** 九项实施及逐任务独立审查已完成；整体审查的四项Important已修复并通过独立复审，本地最终1,550项测试及七场景验收通过。没有遗留Critical或Important发现；保留一项非阻断回退测试增强，以及报告中明确标注的两组未完成搜索。未验证远端CI或Windows，未提交、推送或发布。保留原计划步骤作为可追溯清单，执行中的裁定与最终数据见本次验收记录。

## Global Constraints

- 仅在现有 `feature/v5-unified-workspace` 工作树实施：`/Users/tony_huang/Programs/SZU-Badminton-Draw/.worktrees/v5-unified-workspace`。
- 不修改 5.0 存档结构，不迁移 4.6 存档，不覆盖用户提供的文件，不自动提交、推送或发布。
- 不得自动降低休息时间、提高每日场次上限、缩短比赛时长、改变晋级关系或返回带冲突的半成品。
- 场地、裁判、比赛先后顺序、最短休息、每日上限及显式锁定均为硬约束。
- 日期使用真实日期顺序；休息使用跨日绝对时间；保留现有秒级边界与基线时间语义。
- 有限分支或预算用尽只能说明本次搜索未完成，不是数学上的不可行证明。
- Windows/macOS 分别记录实际验证状态；不把本地 macOS 成功写成 Windows CI 通过。
- 各任务遵循先失败测试、再最小实现、再回归；任务末尾是本地审查检查点，不自动执行 Git 提交。

## Review Focus

- `int.MaxValue` 每日上限、精确秒级时段与容量边界：不溢出、不因取整误拒绝（任务 1、3）。
- 胜败互斥、同名不同身份、双打重复身份与赛果收窄：不能把互不相容的路径叠加，也不能复用旧版本缓存（任务 2、4）。
- 原基线日期/场地被删除，或锁定后继的前驱尚未放置：未锁定位置可重新安排，锁定和先后顺序始终有效（任务 4、5）。
- 计算期间切换工作区、外部修订变化、取消或保存失败：不覆盖原文件，不清空当前编辑参数（任务 8）。
- 搜索已有合法解，但优化/质量计算预算耗尽：保留完整解；统计未知项不能导出为精确数字或“零冲突”（任务 6、7）。

---

## 文件与职责

下列路径均相对于上述工作树。新文件按职责拆分，不为改名而移动现有文件。

| 部分 | 新建或主要修改文件 | 职责 |
| --- | --- | --- |
| 预算与诊断 | `src/BadmintonDraw.Core/Scheduling/SchedulingRunDiagnostics.cs`、`TournamentSchedulingOptions.cs`、`SchedulingWorkBudget.cs` | 运行时类型、阶段预算、计数和取消；不持久化 |
| 条件证明 | `ConditionalAppearanceProof.cs`、`ConditionalPlayerPaths.cs` | 带见证的相容出场下界、上界、分量证明 |
| 预检 | `TournamentPlayerCapacity.cs`、`src/BadmintonDraw.Core/ScheduleResourceSettings.cs` | 单人容量与精确资源容量必要条件 |
| 有界验证 | `TournamentPlacementValidator.cs`、`TournamentPlacementValidator.Bounded.cs`、`TournamentSearchState.cs` | 共享硬规则、索引、撤销与缓存 |
| 搜索 | `TournamentScheduler.cs`、`TournamentScheduleSearch.cs`、`TournamentCandidateEnumeration.cs` | 保留现有候选时间，有界回溯与分支恢复 |
| 均衡 | `TournamentPlacementScorer.cs`、`TournamentScheduleBalancer.cs`、`TournamentScheduleSpreader.cs` | 按容量归一化的全局负荷及合法后处理 |
| 质量与导出 | `TournamentScheduleQualityAnalyzer.cs`、`TournamentSchedulingResult.cs`、`src/BadmintonDraw.Excel/WorkspaceScheduleQualityExcelWriter.cs` | 精确值/上下界、有限概率计算、正确展示 |
| 工作流与界面 | `src/BadmintonDraw.Workflows/Tournaments/TournamentWorkspaceWorkflow.Scheduling.cs`、`WorkspaceCommandResult.cs`、`src/BadmintonDraw.Desktop/ViewModels/ScheduleSetupPageViewModel.cs`、`Views/ScheduleSetupPage.axaml` | 运行结果透传、简明提示、失败不保存 |
| 验收 | 核心与 Desktop 测试、`tools/BadmintonDraw.Acceptance`、`docs/acceptance/v5-scheduling-2026-10-03.md` | 合成及去标识化回归、证据与平台边界 |

依赖顺序：`1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9`。任务 8 的界面测试调研可以并行，涉及共享接口的实现按顺序集成。

测试命令约定：下面“运行过滤 A、B”均执行 `dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj -c Release --filter "FullyQualifiedName~A|FullyQualifiedName~B"`，替换为列出的实际类名；Desktop 测试将项目换为 `tests/BadmintonDraw.Desktop.Tests/BadmintonDraw.Desktop.Tests.csproj`。红灯步骤须确认失败来自新增契约；绿灯步骤须 exit=0、0失败，不能以跳过测试代替。

## Task 1：建立有限计算与运行诊断契约

**Files:** 新建文件表中的三个预算/诊断文件；修改 `SchedulingFailure.cs`、`TournamentSchedulingResult.cs`；新增 `src/BadmintonDraw.Core/Properties/AssemblyInfo.cs` 与 `tests/BadmintonDraw.Tests/SchedulingWorkBudgetTests.cs`。

**Interfaces:**

- 公共 `SchedulingRunPhase`：`Context, Preflight, Search, Validation, Optimization, Quality`。
- 公共 `SchedulingPreflightStatus`：`NotRun, NoContradictionFound, ProvenInfeasible, Unknown`。
- 公共 `SchedulingFailureKind`：`InvalidInput, ProvenInfeasible, SearchIncomplete, ValidationIncomplete, Canceled`。
- 公共记录 `SchedulingRunDiagnostics(SchedulingRunPhase Phase, SchedulingPreflightStatus PreflightStatus, SchedulingFailureKind? FailureKind, IReadOnlyDictionary<SchedulingRunPhase,long> UsedWorkUnits, TournamentResourcePlan Resources, TournamentSchedulingPolicy Policy)`，另有可空 init 属性 `SchedulingRunPhase? ExhaustedPhase`，只保存不可变快照。
- `SchedulingFailure.Diagnostics` 与 `TournamentSchedulingResult.Success.Diagnostics` 新增可空 init 属性，保留旧构造参数和旧调用方。
- 公共 `TournamentSchedulingOptions`：`long ContextWorkUnits, PreflightWorkUnits, SearchWorkUnits, ValidationWorkUnits, OptimizationWorkUnits, QualityWorkUnits`，`int MaxDecisionAlternatives, MaxRollbackDepth, MaxBacktracks, MaxCacheEntries`，均为 init 属性；提供静态 `Default`。非法负值拒绝，0 允许用于终止测试。
- 内部 `SchedulingWorkBudget(TournamentSchedulingOptions options, CancellationToken cancellationToken)`；`bool TrySpend(SchedulingRunPhase phase, long units = 1)`、`long Remaining(SchedulingRunPhase phase)`、`bool IsCanceled`、`IReadOnlyDictionary<SchedulingRunPhase,long> UsedWorkUnits`。阶段耗尽不重置，所有选手共享该阶段预算。

- [x] 编写失败测试：`ZeroBudgetNeverSpends`、`NegativeSpendIsRejected`、`LongLimitDoesNotOverflow`、`CancellationStopsAllPhases`、`OnePlayerCannotResetRequestBudget`、`DiagnosticSnapshotDoesNotAliasMutableCollections`。例如：

  ```csharp
  Assert.False(budget.TrySpend(SchedulingRunPhase.Search, 1)); // Search 预算为 0
  Assert.Equal(0, budget.UsedWorkUnits[SchedulingRunPhase.Search]);
  ```

- [x] 运行 `dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj -c Release --filter FullyQualifiedName~SchedulingWorkBudgetTests`；先确认缺少上述接口导致失败，而不是测试环境故障。
- [x] 实现类型、快照及计数；使用检查过的减法比较余额，不先累加可能溢出的工作量。仅对 `BadmintonDraw.Tests` 开放内部可见性，避免新增反射测试。
- [x] 首轮默认配置设为：Context 5,000,000、Preflight 1,000,000、Search 30,000,000、Validation 10,000,000、Optimization 5,000,000、Quality 2,000,000 工作单位；64 个候选/决策、64 层回退、4,096 次回退、32,768 条缓存。任务 9 可依据证据校准并记录，不改变任何业务约束；不得把单位数宣传为毫秒承诺。
- [x] 重跑本任务测试至通过，检查旧结果构造调用仍可编译；记录本地检查点。

## Task 2：条件路径证明返回可核验见证

**Files:** 新建 `src/BadmintonDraw.Core/Scheduling/ConditionalAppearanceProof.cs`；修改 `ConditionalPlayerPaths.cs`、`TournamentPlayerCapacity.cs` 中原预算适配器；新增 `tests/BadmintonDraw.Tests/ConditionalAppearanceProofTests.cs`。

**Interfaces:**

- 内部 `AppearanceGroup(Guid MatchId, IReadOnlyList<ConditionalPlayerPath> Alternatives)`。
- 内部 `AppearanceWitness(IReadOnlyList<Guid> MatchIds, IReadOnlyDictionary<Guid,bool> Conditions)`。
- 内部 `AppearanceBounds(int LowerBound, int UpperBound, AppearanceWitness Witness, bool IsExact, bool BudgetExhausted)`。
- 内部 `ConditionalAppearanceProof.Prove(IReadOnlyList<AppearanceGroup> groups, int stopAt, SchedulingWorkBudget budget, SchedulingRunPhase phase) : AppearanceBounds`。
- 保留原 `ProveAtLeast`/`MaximumAppearances` 入口供旧调用与对照测试使用；新增生成路径全部使用上述有界入口。

- [x] 编写 `IndependentProjectsCombineEightSevenSixWitness`、`AlternativePathsForOneMatchStayInOneComponent`、`WinnerAndLoserAreNotDoubleCounted`、`UnconditionalMatchCountsOnce`、`ZeroBudgetIsUnknown`。测试夹具在同一测试文件定义 `IndependentChains(params int[] lengths)`，每条链使用独立条件变量；断言：

  ```csharp
  Assert.Equal(21, proof.LowerBound); // lengths = [8, 7, 6], stopAt = 21
  Assert.Equal(21, proof.Witness.MatchIds.Distinct().Count());
  Assert.True(WitnessSatisfiesGroups(groups, proof.Witness));
  ```

- [x] 运行核心测试项目，过滤 `ConditionalAppearanceProofTests`，确认失败。
- [x] 实现按“比赛组—条件变量”关联的连通分量；同组所有替代路径不能拆开。仅合并变量不相交分量的见证；分量内有界 DFS/记忆化，最多 256 组递归，超过边界保留可靠上下界并返回未知。分组、排序、复制、条件比较与缓存写入均计费。
- [x] 在该测试文件定义独立辅助方法 `int EnumerateMaximum(IReadOnlyList<AppearanceGroup> groups)` 和 `bool WitnessSatisfiesGroups(IReadOnlyList<AppearanceGroup> groups, AppearanceWitness witness)`；对小于等于8个条件变量、固定随机种子生成的100例穷举，断言 `LowerBound <= exact && exact <= UpperBound`，见证无重复比赛且每个条件相容。证据达到阈值可提前退出，但不得宣称精确最大值。
- [x] 同时运行 `ConditionalAppearanceProofTests|TournamentPlayerCapacityTests`；调整预算遍历测试只验证共享预算与未知语义，不固化旧选手遍历顺序。记录检查点。

## Task 3：精确容量预检与明确的不可行证据

**Files:** 修改 `TournamentPlayerCapacity.cs`、`TournamentScheduler.cs`、`GraphSchedulingCandidates.cs`、`ConditionalPlayerPaths.cs`、`src/BadmintonDraw.Core/ScheduleResourceSettings.cs`；新增 `SchedulingCapacityEvidence.cs`、`tests/BadmintonDraw.Tests/TournamentCapacityPreflightTests.cs`；保留并扩展 `TournamentPlayerCapacityTests.cs`。

**Interfaces:**

- 公共 `SchedulingCapacityEvidence(string Kind, string? PlayerKey, string? PlayerName, long RequiredLowerBound, long CapacityUpperBound, IReadOnlyList<Guid> WitnessMatchIds, IReadOnlyDictionary<Guid,bool> OutcomeConditions)`；`Kind` 为 `PlayerDailyCap, PlayerTime, ResourceTime`，时间证据使用 ticks 并在展示层格式化。
- 内部 `SchedulingPreflightResult(SchedulingPreflightStatus Status, SchedulingCapacityEvidence? Evidence, IReadOnlyList<SchedulingViolation> Violations)`；`TournamentPlayerCapacity.Check(GraphSchedulingCandidates context, SchedulingWorkBudget budget)` 返回此类型。
- `SchedulingFailure.CapacityEvidence` 新增可空 init 属性。
- `ScheduleResourceCalculator.CalculateDayCapacityTicks(TournamentResourcePlan resources, ScheduleDaySettings day) : long`；现有分钟显示接口保留。
- `TournamentScheduler.Generate(TournamentSchedulingRequest request, TournamentSchedulingOptions options, CancellationToken cancellationToken = default)` 新增公共重载；原无 options 重载转发默认配置，原内部 `Generate(request, int capacityProofWorkUnits)` 保留适配测试含义。

- [x] 新增 `FourDaysRejectCompatibleTwentyOneBeforeSearch`、`MoreCourtsDoesNotFixPlayerTimeCapacity`、`MixedDurationUsesMinimumNotDefaultThirty`、`ExactTicksDoNotCauseFalseCapacityRejection`、`InvalidInputsAndLocksStillTakePrecedence`。核心边界断言：

  ```csharp
  Assert.Equal(SchedulingFailureKind.ProvenInfeasible, failure.Diagnostics!.FailureKind);
  Assert.Equal(0, failure.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Search]);
  Assert.True(failure.CapacityEvidence!.RequiredLowerBound > failure.CapacityEvidence.CapacityUpperBound);
  ```

- [x] 运行过滤 `TournamentCapacityPreflightTests|TournamentPlayerCapacityTests`，确认先失败。
- [x] 精确积分场地/裁判边界的并发容量；用 checked/有界 `long` ticks 运算。单人容量为按日 `min(cap, floor((L+r)/(d+r)))` 之和；`d` 从解析后的该选手可能比赛取最小值。仅将任务 2 的见证下界与容量上界比较，未知继续搜索，不生成不可行结论。
- [x] 将预算传入上下文构建和条件解析：保留原构造器，增加内部 `bool GraphSchedulingCandidates.TryCreate(TournamentSchedulingRequest request, SchedulingWorkBudget budget, out GraphSchedulingCandidates? context)`。false仅表示预算耗尽/取消且context为null；输入违规在true时通过context.InputViolations返回，不能当成预算失败。节点扫描、路径展开、冲突矩阵都计费，避免在预检前无限计算。只在构建完整时发布context。
- [x] 输入与锁定校验在预检之前；锁定校验暂复用现有规则，任务 4 替换成有界共享入口。超大每日上限不先做 `int cap + 1`；先用出场组数确定是否需要证明。
- [x] 重跑全部容量/失败测试。新增 `30/30/240 → 4`、`20/30/240 → 5`、cap=4 时取4、r=0、int.MaxValue 的参数化测试；记录检查点。

## Task 4：可撤销索引和同口径有界验证

**Files:** 新建 `TournamentSearchState.cs`、`TournamentPlacementValidator.Bounded.cs`；修改 `TournamentPlacementValidator.cs`、`GraphSchedulingCandidates.cs`；新增 `tests/BadmintonDraw.Tests/TournamentBoundedValidationTests.cs`、`TournamentSearchStateTests.cs`。

**Interfaces:**

- 内部 `TournamentSearchState(GraphSchedulingCandidates context, IReadOnlyDictionary<Guid,MatchPlacement> initialPlacements)`；只读 `Placements`、`UsedTicksByDay`；`void Add(MatchPlacement placement)`、`MatchPlacement Remove(Guid matchId)`、`IReadOnlyDictionary<Guid,MatchPlacement> Snapshot()`。锁定位置禁止 Remove。
- 内部 `BoundedValidationStatus { Valid, Invalid, Unknown }` 与 `BoundedPlacementValidation(BoundedValidationStatus Status, IReadOnlyList<SchedulingViolation> Violations)`。
- 验证器新增 `ValidatePlacementBounded(MatchPlacement candidate, TournamentSearchState state, SchedulingWorkBudget budget, SchedulingRunPhase phase)` 和 `ValidateScheduleBounded(TournamentSearchState state, SchedulingWorkBudget budget, SchedulingRunPhase phase, bool requireComplete = true)`，返回上面结果。
- 保留公共 `ValidatePlacement/ValidateSchedule` 的签名与精确语义；抽取共同硬规则，不能维护两份各自演化的规则实现。

- [x] 写 `AddRemoveRestoresAllIndexes`、`DailyCapCacheInvalidatesAfterRollback`、`BoundedValidatorMatchesExactOracle`、`UnknownNeverMeansValid`、`LockedSuccessorAllowsLaterPredecessorPlacement`。例如：

  ```csharp
  var before = state.Snapshot();
  state.Add(candidate); state.Remove(candidate.MatchId);
  Assert.Equal(before.OrderBy(x => x.Key), state.Snapshot().OrderBy(x => x.Key));
  Assert.Equal(exact.IsValid, bounded.Status == BoundedValidationStatus.Valid); // 充足预算
  ```

- [x] 运行过滤 `TournamentBoundedValidationTests|TournamentSearchStateTests`，确认失败。
- [x] 按场地、日期和选手维护索引。每日出场证明缓存使用精确规范化身份与比赛组集合键，不能只比较可能碰撞的哈希；同一个 context 内有效，容量达到上限时淘汰旧条目，不重置预算。每候选的日期检查和时间检查跨场地复用。
- [x] 上下文只构建一次；策略默认值解析改为传给 scorer/最终 schedule，不为补齐策略重新展开图。原输入策略保留用于判断哪些偏好是用户明确设置的。
- [x] 将锁定初始检查、搜索候选检查和最终检查接入有界入口；Unknown 不放行，最终验证阶段使用预留 Validation 预算。锁定后继缺少未排前驱的初始例外仅限 MissingPlacement，其他错误不得忽略。
- [x] 固定种子生成合法部分赛程，再加入候选，对照旧精确验证器；覆盖裁判时窗、关闭场地、跨日休息、胜败条件、身份错配、删除的旧基线资源。运行全部 `TournamentScheduler*` 与 `TournamentSchedulingFailureTests`，记录检查点。

## Task 5：保存决策分支并有界回溯

**Files:** 新建 `TournamentCandidateEnumeration.cs`、`TournamentScheduleSearch.cs`；修改 `TournamentScheduler.cs`；新增 `tests/BadmintonDraw.Tests/TournamentScheduleBacktrackingTests.cs`；扩展 `TournamentSchedulerSearchEquivalenceTests.cs`。

**Interfaces:**

- 内部 `TournamentCandidateEnumeration.Starts(ScheduleDaySettings day, Guid matchId, GraphSchedulingCandidates context, IReadOnlyDictionary<Guid,MatchPlacement> placements) : IEnumerable<TimeOnly>`；提取现有 CandidateStarts，保留全部边界语义。
- 内部 `TournamentSearchResult(IReadOnlyDictionary<Guid,MatchPlacement>? CompletePlacements, IReadOnlyDictionary<Guid,MatchPlacement> DiagnosticPlacements, IReadOnlyList<SchedulingViolation> Violations)`。
- 内部 `TournamentScheduleSearch.Run(TournamentPlacementValidator validator, TournamentSearchState state, TournamentPlacementScorer scorer, SchedulingWorkBudget budget, TournamentSchedulingOptions options) : TournamentSearchResult`。
- `CompletePlacements` 只在完整验证通过后赋值；`DiagnosticPlacements` 仅用于失败统计，不作为可保存结果返回。

- [x] 编写具体回溯夹具，使用Compact、30分钟、休息0、每日上限3。第一天14:00—15:00有A/C两片场地，A在14:30后关闭，C在14:30前关闭；第二天14:00—14:30仅A。节点1为X对Y，节点2为P对Q，节点3为节点2胜者对X，锁定节点3在第一天14:30的C。节点1/2深度与冲突度相同，按Order先选节点1；旧贪心占用第一天A后无处放节点2。合法见证为节点2第一天14:00 A、节点3第一天14:30 C、节点1第二天14:00 A。先运行旧实现确认该夹具确实失败。
- [x] 测试 `RecoversAConstructiveGreedyTrapAcrossDays`、`TinyRecoveryBudgetReportsIncomplete`、`RollbackNeverMovesLocks`、`RepeatedRequestIsDeterministic`；断言新结果包含全部节点且公共完整验证无违规，tiny budget 无 Success。
- [x] 使用决策栈，保存节点、候选排序、已尝试游标和状态快照标记；回退撤销实际决策后缀，不撤销锁定。每帧先保留每个有效日期至多两个最佳候选，再按分数补足至64；超出截断明确属于受限搜索。计费候选生成、评分、验证和回退，不只计成功放置。
- [x] 遇到有界验证 Unknown 时允许有预算的其他候选继续探索，不能将该候选记为确定违规；全局耗尽返回 SearchIncomplete 或 ValidationIncomplete，并保留前一次完整验证过的解（如存在）。取消优先返回 Canceled。
- [x] 运行 `TournamentScheduleBacktrackingTests|TournamentSchedulerSearchEquivalenceTests|TournamentSchedulingFailureTests`；保留基线秒级时间、日期顺序、首场地稳定择优。旧失败具体位置断言如因找到合法解而失效，改为完整约束断言并注明原因；记录检查点。

## Task 6：让均衡策略真正考虑跨日容量

**Files:** 修改 `TournamentPlacementScorer.cs`、`TournamentScheduleSpreader.cs`、`TournamentScheduler.cs`；新增 `TournamentScheduleBalancer.cs`、`tests/BadmintonDraw.Tests/TournamentScheduleBalancingTests.cs`。

**Interfaces:**

- `TournamentPlacementScorer.ScoreBalancedDelta(MatchNode node, MatchPlacement placement, TournamentSearchState state) : decimal`，内部计算容量归一化的全局平方偏差增量。
- 内部 `SchedulingCandidateScore(long ExplicitPreference, decimal LoadDelta, long Secondary)` 实现按字段顺序的词典序比较；`TournamentPlacementScorer.Rank(MatchNode node, MatchPlacement placement, TournamentSearchState state)` 返回该类型，供候选排序调用。Compact等其他策略以原Score作为排序主值；现有long SoftScore只保留为报告指标，不用于截断新的decimal均衡评分。
- `TournamentScheduleBalancer.Improve(TournamentPlacementValidator validator, TournamentSearchState state, SchedulingWorkBudget budget, TournamentSchedulingOptions options) : IReadOnlyDictionary<Guid,MatchPlacement>`；输入已完整验证，输出只可能为已验证的原方案或改进方案。
- 保留现有 Score 对 Compact、FinalsDayFriendly、Custom 的公开行为边界；Balanced 评分把容量偏差放在普通时间/基线偏好之前，显式阶段、决赛与自定义目标单独计入。

- [x] 写 `BalancedUsesThreeTwoTwoTwoCapacityWeights`：18 场独立30分钟比赛，四天容量权重3:2:2:2，无其他偏好，预期6/4/4/4；写 `SymmetricDaysDifferByAtMostOneMatch`、`CompactStillFinishesEarlier`、`UnevenBaselineDoesNotFreezeBalancedRegeneration`、`ZeroCapacityDayIsExcluded`。
- [x] 运行过滤 `TournamentScheduleBalancingTests`，确认不均衡旧行为导致失败。
- [x] 对默认 Balanced 使用 `Σ(usedTicks - targetTicks)^2 / capacityTicks`，目标为总时长按有效容量比例分配；比较增量而非仅超额惩罚。使用 decimal 或等价的防溢出有理计算，不把带负号的改善增量直接塞入会截断精度的旧 long 权重。
- [x] 节点评分按明确优先级比较：硬规则先通过；显式用户软规则、默认均衡目标、普通时间/基线偏好分层。无用户指定的默认阶段/决赛规则不能压倒均衡目标；自定义目标仍按其原语义处理。均衡测试均明确去除显式偏好。
- [x] 后处理尝试稳定顺序的单场跨日移动和两场交换，每次全体验证并仅接受全局目标变好者；涉及依赖时不能只验证被移动的一场。优化耗尽保留原完整解。现有 Spread 接受相同预算与验证口径，禁用无界后验检查。
- [x] 增加 `OptimizationExhaustionKeepsValidatedSolution`、锁定/依赖/休息阻止交换用例；运行全部 scheduling 测试，记录检查点。

## Task 7：质量计算有界，Excel 不伪造精确值

**Files:** 修改 `TournamentScheduleQualityAnalyzer.cs`、`TournamentSchedulingResult.cs`、`src/BadmintonDraw.Excel/WorkspaceScheduleQualityExcelWriter.cs`、`tools/BadmintonDraw.Acceptance/V5ArtifactValidator.cs`；新增 `tests/BadmintonDraw.Tests/TournamentBoundedQualityTests.cs`；扩展 `WorkspaceScheduleQualityWriterTests.cs`、`WorkspaceScheduleQualityFixture.cs` 和 `TournamentPlacementValidatorTests.cs`。

**Interfaces:**

- 运行时 `TournamentPlayerDailyLoadForecast.MaximumCount` 改为 `int?`，原传入 int 的构造代码仍可用；新增 `int MaximumLowerBound`、`int MaximumUpperBound` init 属性。null 表示没有精确最大值；旧 `IsExact` 仍只表示概率分布的精确性。
- `TournamentScheduleQuality` 增加 `bool HardValidationComplete`（默认 true）、`bool PlayerAnalysisComplete`（默认 true）init 属性。
- `TournamentScheduleQualityAnalyzer.AnalyzeBounded(TournamentSchedulingRequest request, TournamentSearchState state, SchedulingWorkBudget budget, bool hardValidationAlreadyPassed) : TournamentScheduleQuality` 为内部生成入口。公共 Analyze 使用默认预算、先执行有界硬检查；未知不得声称零违规已验证。

- [x] 编写 `ValidatedScheduleSurvivesQualityBudgetExhaustion`、`IncompleteMaximumIsNullWithBounds`、`ProbabilityBudgetIsSharedAcrossPlayers`、`NoProbabilityMassIsExposedForPartialEnumeration`。断言：

  ```csharp
  Assert.Null(load.MaximumCount);
  Assert.True(load.MaximumLowerBound <= load.MaximumUpperBound);
  Assert.Null(load.ExpectedCount); Assert.Null(load.ProbabilityAtOrAboveLimit);
  Assert.False(load.IsExact);
  ```

- [x] 运行过滤 `TournamentBoundedQualityTests|WorkspaceScheduleQualityWriterTests`，确认新未知语义测试失败。
- [x] 复用任务4的精确状态缓存；硬检查已通过时不重复完整验证。最大值计算复用任务2上下界；概率枚举按总工作量而非单个选手的变量数限制，只有完整枚举才输出分布/期望/概率。
- [x] 排序按“精确最大值或已证明下界”并稳定比较身份；展示明确区分。Excel“兼容最大出场数”在 MaximumCount 为 null 时写“未完成计算”，旁列或备注显示下/上界；修正总览中“最大值为兼容路径最大值”的无条件说明。
- [x] 增加 `UnknownMaximumIsNotWrittenAsZeroOrUpperBound`、`IncompleteValidationDoesNotClaimZeroConflicts` 的工作簿读回测试；保留“最大次数精确但概率未知”的19变量旧用例。验收Snapshot使用有界质量入口并记录完整性，不能重新调用无界分析来生成摘要。运行质量导出及完整材料包回归，确认没有向存档序列化新增质量字段；记录检查点。

## Task 8：工作流安全透传与可理解的提示

**Files:** 修改 `src/BadmintonDraw.Workflows/Tournaments/TournamentWorkspaceWorkflow.Scheduling.cs`、`WorkspaceCommandResult.cs`；修改 `src/BadmintonDraw.Desktop/ViewModels/AppShellViewModel.cs`、`ScheduleSetupPageViewModel.cs`、`Views/ScheduleSetupPage.axaml`；扩展 `tests/BadmintonDraw.Tests/TournamentSchedulingWorkflowTests.cs`、`tests/BadmintonDraw.Desktop.Tests/ScheduleSetupPageViewModelTests.cs`。

**Interfaces:**

- 工作流增加 `GenerateSchedule(TournamentResourcePlan resources, TournamentSchedulingPolicy policy, long expectedRevision, TournamentSchedulingOptions options, CancellationToken cancellationToken = default)` 重载；原三参数入口保留并使用 Default。
- `WorkspaceCommandResult.SchedulingQuality`、`.SchedulingDiagnostics` 新增可空 init 属性，不加入 workspace 或审计数据结构。
- `AppShellViewModel.LastCommandResult` 新增只读公共属性、内部设置；每次实际开始命令时清空，仅成功且会话未切换时保存当前结果。设置页在Generate成功后取本次结果，再Load；普通失败不能显示上一轮成功摘要。
- 设置页增加 `FailureSummary`、`FailureAdvice`、`SuccessSummary`；失败详细清单继续使用 `FailureTechnicalDetails` 并折叠。成功摘要是本次运行状态，重新打开时从已保存赛程计算每日负荷，不伪造上一轮预算记录。

- [x] 编写 `ProvenPlayerCapacityFailureShowsCountAndUsefulAdvice`、`SearchIncompleteNeverClaimsImpossible`、`FailureRetainsCurrentEditorInputs`、`SuccessfulGenerationShowsPerDayUtilization`；核心文案断言包含“原赛程没有改变”，且单人上限矛盾不把“增加场地”当作解决办法。
- [x] 运行 Desktop 对应过滤测试，确认失败。
- [x] 按失败类型呈现：输入错误沿用字段提示；已证明矛盾显示证据/设置/算式；搜索或验证未完成显示不确定性。未排场次和原因列表放入默认折叠详情，普通摘要不使用 GUID、不列出数百行比赛。建议不自动改参数。
- [x] 在核心阶段及保存前检查取消；取消以 `schedule.generation-canceled` 的 WorkspaceCommandException 返回且 `SchedulingFailure.Diagnostics.FailureKind = Canceled`。进入原子持久化提交之后不尝试中途取消替换；取消与提交同时发生时，以现有提交结果为准。首版不另加桌面取消按钮。
- [x] 保留捕获会话、源身份及提交时修订检查；新增 `CanceledBeforeCommitDoesNotMutateStore`、`BudgetFailureKeepsOriginalArchiveBytes`、`StaleWorkspaceAfterSuccessfulSearchDoesNotCommit`、`SaveFailureKeepsOriginalSchedule`，复用现有RejectUnchanged核对hash/会话/Mutate次数。使用现有假store的读/写回调控制竞态，不依赖Thread.Sleep猜时序；提交后读回失败仍按Committed=true报告，不能被迟到的取消覆盖为“没有保存”。
- [x] 运行 `TournamentSchedulingWorkflowTests` 和 `ScheduleSetupPageViewModelTests`；构建 Desktop 检查 AXAML 绑定。确认日期置顶、时间选择器、场馆选择等既有体验未变；记录检查点。

## Task 9：原规模回归、基准校准及证据交付

**Files:** 新建 `tests/BadmintonDraw.Tests/Fixtures/PresidentsCup276.json`、`PresidentsCupSchedulingRegressionTests.cs`、`tools/BadmintonDraw.Acceptance/V5SchedulingAudit.cs`、`docs/acceptance/v5-scheduling-2026-10-03.md`；修改测试 csproj 的夹具复制声明、Acceptance `Program.cs`、`docs/scheduling.md`，只在必要时校准 `TournamentSchedulingOptions.cs`。

**Interfaces:**

- `V5SchedulingAudit.Run(string inputPath, string outputDirectory) : int`，独立模式由 `Program.cs` 严格解析 `--scheduling-audit <input.szbd> --output <new-or-empty-directory>`；保留现有无参数七场景验收行为。
- 先复制输入到新输出目录，后续只打开副本；拒绝输出路径等于或包含原输入目标的危险写法。记录原文件前后 SHA-256，失败也保留诊断产物。
- JSON 回归夹具仅包含去标识化比赛图、稳定虚拟身份、项目结构及资源/策略；不包含原姓名、学号、桌面路径、源文件哈希或审计历史。

- [x] 先写夹具不变量测试：276 场，235 个身份，113 人兼项、37 人三项，男单163/男双74/混双39；固定记录同一虚拟身份的相容21场见证。检查夹具中所有身份均使用新生成的 `P0001` 等代号，节点ID也一致映射。
- [x] 从用户提供的5.0存档副本生成严格白名单夹具；不将整份存档或原有审计字段转为JSON提交。逐项比较去标识化前后的比赛依赖、兼项关系和条件路径见证；用源数据手工计数加独立检查，不能只让同一段转换代码自证正确。
- [x] 写 `DefaultFourDayRequestRejectsBeforeSearch`、`IncreasingCourtsDoesNotRemoveWitness`、`KnownFeasibleLargeSchedulePassesAllHardRules`。已知可行大用例必须提供可独立验证的完整位置见证；同时保留现有292场成功用例，不拿未知四日组合冒充可行基线。
- [x] 审计模式运行原图的三组固定设置：四日30分钟/休息30/每日4；同设置每天48片场地；四日30分钟/休息0/每日8。第三组只记录 Success 或 Incomplete/ProvenInfeasible 及证据，不预设成功。任何Success都另用公共精确验证器复核，复核过程单独受外部进程超时保护，超时记为未完成验证；另用测试侧独立条件赋值检查器核验见证及小型图，明确原有Snapshot的独立场地/依赖检查并不等于独立覆盖了全部条件选手规则。
- [x] 对可行用例同时运行 Compact 与 Balanced，保存每日日数/分钟/利用率、输入、终止状态、预算计数、耗时、缓存峰值和独立校验结果。质量预算不足须可见但不能毁掉已验证赛程。Balanced本次Incomplete，没有完整赛程的每日利用率记为不适用。
- [x] 校准任务1默认预算：优先消除重复计算；仅在固定病例的工作量证据支持时改限额。验收报告记录实际常量与本机耗时，不把只提高预算当作算法修复，也不承诺未证明组合一定可排。
- [x] 执行以下本地最终验证并记录实际结果：

  ```bash
  dotnet restore BadmintonDraw.sln --locked-mode -p:Configuration=Release
  dotnet build BadmintonDraw.sln -c Release --no-restore
  dotnet test BadmintonDraw.sln -c Release --no-build --verbosity normal
  bash scripts/check-vulnerable-packages.sh BadmintonDraw.sln
  dotnet run --project tools/BadmintonDraw.Acceptance/BadmintonDraw.Acceptance.csproj -c Release --no-build
  git diff --check
  ```

  用户样本审计用新输出目录单独运行；原始绝对路径只用于本地命令，不写入仓库文档。开始前核对 dotnet 版本、工作树和锁文件；不自动升级SDK或依赖来消除失败。

- [x] 更新 `docs/scheduling.md`：证明与未知、回溯边界、容量均衡、质量统计未知项。新建独立验收报告，不改写历史 `docs/acceptance/v5.0.0.md` 的冻结结论。远端CI待用户提交/PR后才可验证；不擅自触发发布。
- [x] 独立审查全部差异，重点复核容量证明的可靠性、无界计算残留、缓存失效、保存边界和统计诚实性；修复审查发现并重跑受影响测试。交付修改摘要、测试证据、原文件哈希核对及尚未解决的输入，留给用户自行提交。

## 计划自检与执行交接

- 设计第4节安全边界由任务3—8的输入、硬验证、锁定和工作流测试覆盖；没有靠放松约束通过验收的任务。
- 预检状态与搜索状态分开；任务1定义共享诊断，任务3提供证据，任务5/8不得把 Unknown 翻译成不可行。
- 任务4拥有验证与缓存口径，任务5/6/7只能调用该入口，不各自复制规则。
- 精确最大值与概率精确性分离；任务7同步模型、排序、Excel文案和读回测试。
- 用户原材料仅副本读入，运行时诊断不入库；4.6和GitHub不在修改范围。
- 已完成逐任务实施、独立审查及整体审查闭环；实际通过项和未验证的平台边界以本次验收记录为准。

建议采用逐任务实现与独立审查：该改动涉及证明正确性、搜索状态恢复和存档安全，逐项审查能在错误进入下一层前发现问题。也可由主代理连续实施全部任务，再进行一次独立总审查；接口与验收要求不变。
