# macOS 版开发说明

本文记录跨平台桌面版的开发路线。现有 Windows 版仍保留在 `src/BadmintonDraw.App`，macOS/Windows/Linux 跨平台桌面版位于 `src/BadmintonDraw.Desktop`。

## 技术路线

当前 Windows GUI 使用 WPF：

- `src/BadmintonDraw.App`：`net8.0-windows` + `UseWPF=true`
- `src/BadmintonDraw.Core`：`net8.0`
- `src/BadmintonDraw.Excel`：`net8.0`

WPF 只能运行在 Windows 上，因此 macOS 版不直接复用 WPF 界面。跨平台 UI 使用 Avalonia，继续复用现有 `Core` 和 `Excel` 项目。

当前 `BadmintonDraw.Desktop` 已接入：

- 生成名单模板
- 选择并导入参赛名单
- 自动识别单打、双打、团体
- 设置赛制、小组数、随机数种子
- 预览总分组、首轮赛、轮空或直接晋级
- 导出抽签结果 Excel、JPG、极清透明 PNG、A4 PDF 和全部格式
- 多比赛日赛程编排预览
- 运动广场东馆、至快、至畅等场地预设
- 分界线前/后不同单场耗时和每日最多场次
- 导出完整赛程 Excel、JPG、极清透明 PNG、A4 PDF 和全部格式
- 导出首日材料包，固定包含记录表、赛程安排表、带时间场地对阵表和单场计分表 PDF
- 创建、打开 `.szbd` 赛事存档
- 导入已填写记录表，事务更新赛事存档并导出下一比赛日材料包
- 生成下一比赛日单场比赛计分表 PDF，每场一页横版；团体项目额外生成 Excel 团体赛记分表
- 记录表导入问题逐条确认，并按记录表第一列“序号”定位
- “赛程预览”可打开独立时间场地窗口，支持比赛日切换、缩放、拖拽未完成比赛、目标格三色实时反馈、前序场次连锁影响预览，以及撤销最近一次手动调整
- “多项目赛程编排”菜单：选择多个 `.szbd` 赛事存档加载多项目赛程总表，支持兼项明细、冲突标红、独立窗口查看、拖拽调整、目标格三色实时反馈、按项目内部淘汰树预览连锁影响、按已确定选手身份预览其他项目同日兼项影响、撤销最近一次手动调整、按策略全局自动编排未完成赛程、保存回存档、导出合并材料包和导出多项目排程检查报告

尚未迁移：

- macOS 下中文字体、图片和 PDF 输出的人工视觉验收
- macOS Developer ID 签名、公证和 stapler。当前 `.app` / `.dmg` 只作为未签名内部测试包生成。

## 推荐开发环境

Visual Studio for Mac 已退役，不建议再作为开发环境。macOS 上推荐：

- Rider，桌面 .NET / XAML 体验较完整。
- VS Code + C# Dev Kit + Avalonia 扩展。

需要安装：

```bash
xcode-select --install
brew install --cask dotnet-sdk
dotnet new install Avalonia.Templates
```

如果使用官方 .NET 安装包，也可以从 Microsoft 下载 .NET 8 SDK。项目根目录的 `global.json` 指定 `8.0.100` 并允许 roll forward，因此更高版本 SDK 也可构建。

## 常用命令

在 macOS 主系统中：

```bash
git clone https://github.com/TonyHuang6666/SZU-Badminton-Draw.git
cd SZU-Badminton-Draw
dotnet restore src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj
dotnet build src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj -c Debug
dotnet run --project src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj -c Debug
```

发布一个未签名的 macOS arm64 `.app` 和 `.dmg`：

```bash
VERSION=4.2.0 bash scripts/publish-macos.sh osx-arm64
```

生成文件位于：

```text
artifacts/macos/osx-arm64/SZU Badminton Draw.app
artifacts/macos/osx-arm64/SZU-Badminton-Draw_osx-arm64.dmg
```

Intel Mac 可将 runtime 改为 `osx-x64`。当前脚本生成的是未签名、未公证包；正式公开分发前还需要补 Apple Developer ID 签名、公证和 stapler 流程。内部测试时如果 macOS Gatekeeper 阻止打开，可在 Finder 中右键应用并选择“打开”，或在测试机上对下载后的应用执行 `xattr -dr com.apple.quarantine`。

赛程页的 macOS GUI 已按办赛逻辑排列分段耗时控件：先选择“分界线”，再填写“分界线前设置”，最后填写“分界线后设置”。打开赛事存档后，左侧状态中的“待决”表示整份赛程中尚未产生结果的场次数，而不是仅表示顺延补赛数。

单项目“赛程预览”和多项目赛程编排都支持打开独立时间场地窗口。单项目窗口用于查看和临时调整当前赛程，拖动后右侧逐场列表和后续导出材料会同步更新；多项目窗口用于跨项目审计和调整。两类窗口共享 `ScheduleBoardView` 时间场地面板模型，日期、场地、时间槽、卡片状态、拖拽载荷、缩放边界、定位闪烁和撤销入口保持一致；未完成卡片可在当前比赛日内拖拽，也可右键选择“移动到指定比赛日...”进行跨日移动。拖动悬停时，Avalonia 版会用绿色表示可放、黄色表示可放但有提醒、红色表示硬冲突并禁止落下；右键移动也会执行同一套校验。移动淘汰树前序场次时，会弹出连锁影响预览，列出受影响的直接和间接后续场次；裁判长可选择只移动当前卡片，或让程序把同项目后续依赖场次连锁后移到最近合法空位。多项目下还会单列已确定兼项选手在其他项目里的同日重叠、休息不足或体能提醒；连锁移动只自动调整本项目依赖链，但会用全局场地、兼项和休息约束避开其他项目。误拖后可点“撤销调整”回退最近一次手动移动或整次连锁移动。Avalonia 多项目工作台加载多个存档后默认按“均衡宽松”策略直接全局编排未完成赛程；也可以切换为“紧凑完成”“决赛日友好”或“自定义”，策略切换会立即重排，自定义会展开每日负载率和阶段波次，并按当前操作锚点联动其他参数。跨项目统一收官日作为默认评分项保留，不再占用 GUI 调节面板。macOS 端主面板保留“打开窗口”，缩放集中在独立窗口内；场地较多时建议打开独立窗口，再配合缩放和横向滚动查看。兼项明细支持按最短休息时间排序。

## 迁移原则

1. 不改动抽签核心算法来适配 UI。
2. 新 UI 优先调用 `BadmintonDraw.Workflows` 的共享工作流；必要时再直接使用 `BadmintonDraw.Core` 的数据类型。
3. 4.0 起 Avalonia 版作为 macOS/Windows 的主力桌面版；WPF 版保留为 Windows 备用版本和短期回归对照。
4. 每迁移一个工作流，都要补一条面向共享服务或 Excel 输出的测试，避免 Windows/macOS 两套 UI 行为漂移。
5. macOS 版不要依赖 Windows 专属 API，例如 `System.Windows`、`Microsoft.Win32.OpenFileDialog`、WPF `MessageBox`。

## 后续优先级

1. 增加 macOS Developer ID 签名、公证和 stapler 脚本。
2. 继续用真实 macOS 与 Windows 验收 Avalonia 版中文字体、图片和 PDF 输出。
3. 收敛 WPF 与 Avalonia 的重复 UI 逻辑，把新功能优先下沉到 `Core`、`Excel` 和 `Workflows`。
