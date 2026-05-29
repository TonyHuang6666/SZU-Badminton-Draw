# 深大羽协抽签工具

这是一个面向 Windows 电脑、源代码公开的羽毛球比赛抽签工具，来源于深大羽协曾经使用的 `BFSZU.cpp` 控制台程序。新版本的目标是让非技术干事也能完成比赛抽签，同时让全校同学可以公开查看抽签原理。

## 当前状态

仓库已经改造成分层的 Windows 图形化工具：

- `src/BadmintonDraw.Core`：抽签核心算法，不依赖界面和 Excel。
- `src/BadmintonDraw.Excel`：参赛名单 Excel 导入、结果 Excel 导出、名单模板生成。
- `src/BadmintonDraw.App`：Windows WPF 图形界面。
- `tests/BadmintonDraw.Tests`：不依赖测试框架的基础算法测试。
- `docs`：算法、公平性和使用说明。

## 最终使用方式

1. 打开 `BadmintonDraw.App.exe`。
2. 点击“生成名单模板”，填写参赛名单。
3. 回到软件，选择参赛名单 Excel。
4. 选择比赛模式、项目类型、小组数和随机种子。
5. 点击“预览抽签”检查结果。
6. 点击“导出结果 Excel”，得到 `抽签结果.xlsx`。

导出的结果文件会包含：

- 总分组结果
- 第一轮对阵名单
- 轮空或直接晋级名单
- Excel 排表格式
- 抽签设置与审计信息
- 原始名单

## 公平性设计

新版使用公开随机种子。只要参赛名单、设置、抽签规则和随机种子完全一致，任何人在本地重新运行都可以得到同样的抽签结果。

结果文件会写入：

- 抽签规则
- 随机种子
- 输入哈希
- 生成时间
- 参赛数量、种子数量、小组数量

## 开发环境

建议使用 Windows 电脑和 Visual Studio 2022：

- .NET 8 SDK
- Visual Studio 2022 的“.NET 桌面开发”工作负载

命令行构建：

```powershell
dotnet restore
dotnet build
dotnet run --project tests/BadmintonDraw.Tests
dotnet run --project src/BadmintonDraw.App
```

macOS/Linux 可以查看和测试核心代码，但 WPF 图形界面只能在 Windows 上构建和运行。

详细构建和发布命令见 [docs/build.md](docs/build.md)。

## 历史来源

原始 C++ 控制台程序已经从当前工程中移除，避免干事误用旧入口。需要追溯历史实现时，可以通过 Git 历史或 GitHub 原仓库查看。

## 授权许可

本项目采用双许可证模式：

- 大学、高校羽毛球社团可免费用于非商业校园赛事、社团管理、学生比赛和相关教育或学生组织活动。
- 培训机构、赛事公司、商业活动、付费赛事服务、商业分发或作为付费产品/服务的一部分使用前，必须先取得作者书面授权。

详细条款见 [LICENSE](LICENSE)。历史上已经按其他许可证发布的版本，仍以对应历史版本发布时的许可证为准。
