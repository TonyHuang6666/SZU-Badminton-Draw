# 代码结构审计

本文记录 4.0 后的一次轻量结构审计，目的是帮助后续开发保持边界清晰，而不是追求一次性大重构。

## 当前边界

- `BadmintonDraw.Core`：抽签规则、赛程模型、冲突模型等核心数据与算法。
- `BadmintonDraw.Excel`：名单读取、对阵表、赛程安排表、记录表、计分表、图片/PDF 导出。
- `BadmintonDraw.Workflows`：把 Core 与 Excel 串成可被 UI 调用的业务流程，包括赛事存档、多项目工作台和合并材料包。
- `BadmintonDraw.Desktop`：Avalonia 跨平台 UI。
- `BadmintonDraw.App`：WPF Windows UI。
- `BadmintonDraw.Tests`：核心规则、Excel 输出、工作流和回归测试。

这个分层是健康的。多数新增功能应该先落在 `Core`、`Excel`、`Workflows`，UI 只负责收集输入、展示状态和调用工作流。

## 主要风险

- Avalonia 与 WPF 都有较大的窗口代码，后续容易出现 UI 层重复逻辑。
- 单项目和多项目时间场地表格的显示、拖拽和缩放概念相近，适合逐步抽出共享视图模型。
- `CrossEventConflictWorkflow` 已经承担加载、检测、调整、保存、导出等多种职责，后续可按“加载/调整/导出”拆分。
- `ScheduleExcelWriter` 负责的工作表较多，继续增加材料包细节时要避免单类过度膨胀。
- 文档和 release 说明容易跟版本能力漂移，应把每次重大功能的边界写进 `docs/`。

## 本次低风险整理

- 将 `ScheduleMatchText` 从内部工具改为公开共享工具，允许 `Workflows` 复用胜者/负者占位解析逻辑。
- 移除跨项目合并赛程中的重复 `TryParseOutcomeReference` 实现，减少同类规则在两个位置漂移的风险。
- 增加跨项目合并导出的回归测试，锁定“严重冲突禁止导出”和“同名场次引用带项目名前缀”两条关键行为。

## 后续可拆分点

1. 抽出 `ScheduleBoardViewModel`，让单项目和多项目时间场地表格共享日期、场地、时间槽、卡片状态和缩放数据。
2. 把 `CrossEventConflictWorkflow` 中的导出逻辑拆到单独服务，例如 `CrossEventMergedMaterialsExporter`。
3. 把 `ScheduleExcelWriter` 的记录表、时间场地网格、参数页拆成内部小 writer。
4. 给 docs 增加“版本能力矩阵”，发布前用它检查 README、usage、build、macOS 文档是否同步。
5. 若 WPF 进入维护期，删除或冻结 WPF 里的新功能入口，避免用户看到两套不一致的体验。
