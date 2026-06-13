# 构建与发布

## 开发机准备

安装：

- Visual Studio 2022
- `.NET 桌面开发` 工作负载
- .NET 8 SDK

项目主要依赖：

- ClosedXML：读取名单、生成对阵表和赛程表 Excel。
- SkiaSharp：从 Excel 工作表渲染 JPG、透明 PNG 和 PDF。
- xUnit：测试核心算法、Excel 导入导出和视觉导出。

这些依赖通过 NuGet 管理。发布为自包含 Windows 程序后，普通用户不需要安装 Office、Adobe、Python、MinerU 或其他额外运行环境。

## 本地运行

在仓库根目录执行：

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src\BadmintonDraw.App\BadmintonDraw.App.csproj
```

也可以直接运行跨平台 Avalonia 版：

```powershell
dotnet run --project src\BadmintonDraw.Desktop\BadmintonDraw.Desktop.csproj
```

也可以直接用 Visual Studio 或 Rider 打开 `BadmintonDraw.sln`。Windows WPF 版启动项目是 `BadmintonDraw.App`；跨平台 Avalonia 版启动项目是 `BadmintonDraw.Desktop`。

如果 Visual Studio 正在运行 `BadmintonDraw.App`，重新构建时可能出现 DLL 被锁定。关闭正在运行的抽签工具窗口后重新构建即可；不需要关闭整个 Visual Studio。

## 测试入口

推荐使用：

```powershell
dotnet test tests\BadmintonDraw.Tests\BadmintonDraw.Tests.csproj -c Debug --no-restore -v minimal
```

测试覆盖范围包括：

- 单打、双打、团体名单导入。
- 自动识别项目类型。
- 种子数量和签位保护。
- 淘汰赛首轮赛、轮空、每组出线和决冠军。
- 循环赛矩阵、轮转顺序和同单位先赛。
- Excel 对阵表、赛程表、图片和 PDF 导出。
- 多比赛日赛程编排和带比赛时间/场地的对阵表。
- 对阵记录表导入、逐条提醒、赛事存档和重复导入保护。
- 单项目时间场地窗口拖拽调整。
- 多项目冲突检测、兼项明细、自动调整、合并材料包导出。
- 内置 Noto CJK 字体的中文图片/PDF 导出。

## 发布单文件程序

### Windows

Avalonia Windows 版：

```powershell
dotnet publish src\BadmintonDraw.Desktop\BadmintonDraw.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
```

WPF Windows 版：

```powershell
dotnet publish src\BadmintonDraw.App\BadmintonDraw.App.csproj -c Release -r win-x64 --self-contained true --no-restore /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
```

生成文件位于：

```text
src\BadmintonDraw.Desktop\bin\Release\net8.0\win-x64\publish
src\BadmintonDraw.App\bin\Release\net8.0-windows\win-x64\publish
```

把发布目录中的 `.exe` 发给干事即可使用。4.0 起 WPF 版不再要求压缩包包装；Avalonia Windows 版是首选桌面版，WPF 版保留为 Windows 备用版本。

### macOS

在 macOS 上运行：

```bash
bash scripts/publish-macos.sh osx-arm64
```

生成文件位于：

```text
artifacts/macos/osx-arm64/SZU Badminton Draw.app
artifacts/macos/osx-arm64/SZU-Badminton-Draw_osx-arm64.dmg
```

当前 macOS 包未签名、未公证，适合作为内部测试包。正式公开分发前需要接入 Apple Developer ID 签名、公证和 stapler。

正式发布到 GitHub Release 时，建议：

1. 先跑 `dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --no-restore --verbosity minimal`。
2. 再跑 `dotnet build BadmintonDraw.sln --no-restore --verbosity minimal`。
3. macOS 包使用 `VERSION=x.y.z bash scripts/publish-macos.sh osx-arm64` 生成，并上传 `artifacts/macos/osx-arm64/SZU-Badminton-Draw_osx-arm64.dmg`。
4. Windows 版分别上传 Avalonia 单文件 `.exe` 和 WPF 单文件 `.exe`，WPF 版不用压缩包包装。
5. Release 说明中列出规则化抽签、单项目/多项目赛程编排、多格式导出、赛事存档、记录表导入确认、合并材料包和跨平台桌面版等重要变化。

GitHub CLI 示例：

```bash
gh release create v4.0.0 \
  artifacts/macos/osx-arm64/SZU-Badminton-Draw_Avalonia_macOS_osx-arm64_v4.0.0.dmg \
  artifacts/windows/win-x64/SZU-Badminton-Draw_Avalonia_Windows_win-x64_v4.0.0.exe \
  artifacts/windows/win-x64/SZU-Badminton-Draw_WPF_Windows_win-x64_v4.0.0.exe \
  --title "Release 4.0.0" \
  --notes-file /tmp/szu-badminton-release-4.0.0.md
```

## 示例名单

仓库的 `samples` 目录包含可直接导入软件的虚拟测试名单。它们不会参与自动化测试，但方便读者验证 GUI 流程：

- `深大羽协虚拟双打参赛名单_159人.xlsx`
- `深圳大学虚拟双打参赛名单_29人.xlsx`
- `深圳大学虚拟29学院参赛名单.xlsx`

## 跨平台说明

`BadmintonDraw.Core`、`BadmintonDraw.Excel` 和 `BadmintonDraw.Workflows` 是普通 .NET 项目，可以在 macOS/Linux 上参与构建和测试。Windows WPF GUI 只能在 Windows 上运行；Avalonia GUI 位于 `src/BadmintonDraw.Desktop`，用于 macOS/Windows/Linux 跨平台桌面版。

跨平台维护时应保持：

- 保留 `BadmintonDraw.Core` 作为跨平台核心。
- 让 WPF 和 Avalonia 都调用 `BadmintonDraw.Workflows`，避免 UI 层重复业务逻辑。
- 保持 Excel、图片、PDF 导出逻辑在独立类库中，减少界面迁移成本。
