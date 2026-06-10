# macOS 版开发说明

本文记录跨平台桌面版的开发路线。现有 Windows 版仍保留在 `src/BadmintonDraw.App`，macOS/Windows/Linux 跨平台预览版位于 `src/BadmintonDraw.Desktop`。

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
- 同步导出带比赛时间和场地的对阵表
- 单独导出当日赛程记录表
- 导入已填写记录表并导出下一比赛日记录表

尚未迁移：

- macOS 下中文字体、图片和 PDF 输出的人工视觉验收
- macOS `.app` / `.dmg` 打包、签名、公证

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
bash scripts/publish-macos.sh osx-arm64
```

生成文件位于：

```text
artifacts/macos/osx-arm64/SZU Badminton Draw.app
artifacts/macos/osx-arm64/SZU-Badminton-Draw_osx-arm64.dmg
```

Intel Mac 可将 runtime 改为 `osx-x64`。当前脚本生成的是未签名、未公证包；正式公开分发前还需要补 Apple Developer ID 签名、公证和 stapler 流程。

## 迁移原则

1. 不改动抽签核心算法来适配 UI。
2. 新 UI 优先调用 `BadmintonDraw.Workflows` 的共享工作流；必要时再直接使用 `BadmintonDraw.Core` 的数据类型。
3. WPF 版继续维护，直到跨平台版覆盖完整赛程工作流。
4. 每迁移一个工作流，都要补一条面向共享服务或 Excel 输出的测试，避免 Windows/macOS 两套 UI 行为漂移。
5. macOS 版不要依赖 Windows 专属 API，例如 `System.Windows`、`Microsoft.Win32.OpenFileDialog`、WPF `MessageBox`。

## 后续优先级

1. 在真实 macOS 上验收 ClosedXML 和 SkiaSharp 的中文字体、图片和 PDF 输出。
2. 补齐跨平台版与 WPF 版之间仍有差异的细节交互，例如更精细的显示/隐藏规则。
3. 增加 macOS Developer ID 签名、公证和 stapler 脚本。
