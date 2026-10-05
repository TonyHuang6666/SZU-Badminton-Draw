# 资料归档

本目录按原有日期、源码提交和验证范围保存开发资料。当前程序的功能与操作从[文档中心](../index.md)查阅。

报告中的测试数量、文件哈希、界面状态和实施进度均对应记录当时的对象。复现时使用报告注明的源码提交、参数与环境；新的验证结果应单独记录。

## 验收记录

| 日期／对象 | 资料 | 内容 |
| --- | --- | --- |
| 2026-09-12，4.6.0 | [稳定性验收](acceptance/v4.6.0.md) | 对应版本的构建、赛事样例、材料及平台证据 |
| 2026-09-13，提交 `9c476ab` | [本地候选验收](acceptance/v5.0.0.md) | 七个工作流与规模场景、故障矩阵、产物验证与平台包 |
| 2026-10-03，GUI | [界面本地验证](acceptance/v5-gui-2026-10-03.md) | 分阶段界面调整、测试和人工查看范围 |
| 2026-10-03，排程 | [排程可靠性验收](acceptance/v5-scheduling-2026-10-03.md) | 规模样例、搜索与验证预算、成功和未完成结果 |

## 设计与实施

| 主题 | 设计 | 实施记录 |
| --- | --- | --- |
| 稳定性验收 | [验收规范](superpowers/specs/2026-09-12-v4.6.0-stability-acceptance.md) | [实施计划](superpowers/plans/2026-09-12-v4.6.0-stability-acceptance.md) |
| 赛事工作区 | [架构设计](superpowers/specs/2026-09-13-v5-unified-tournament-workspace-design.md) | [实施计划](superpowers/plans/2026-09-13-v5-unified-tournament-workspace.md)、[实施记录](superpowers/plans/2026-09-13-v5-progress.md) |
| 赛事助手界面 | [界面设计](superpowers/specs/2026-09-20-v5-beginner-friendly-gui-design.md) | [实施计划](superpowers/plans/2026-10-03-v5-gui-implementation.md) |
| 排程可靠性 | [算法设计](superpowers/specs/2026-10-03-v5-global-scheduling-reliability-design.md) | [实施计划](superpowers/plans/2026-10-03-v5-global-scheduling-reliability.md) |

## 发布资料

[4.6.0 发布记录](releases/v4.6.0.md)保存在本目录。[5.0.0 发布说明](../releases/v5.0.0.md)位于发布文档目录。
