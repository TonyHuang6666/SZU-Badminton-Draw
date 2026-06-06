# Windows 构建与发布

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
dotnet run --project src/BadmintonDraw.App
```

也可以直接用 Visual Studio 打开 `BadmintonDraw.sln`，将 `BadmintonDraw.App` 设为启动项目。

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

## 发布单文件程序

```powershell
dotnet publish src\BadmintonDraw.App\BadmintonDraw.App.csproj -c Release -r win-x64 --self-contained true --no-restore /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
```

生成文件位于：

```text
src\BadmintonDraw.App\bin\Release\net8.0-windows\win-x64\publish
```

把发布目录中的程序发给干事即可使用。

正式发布到 GitHub Release 时，建议将发布目录压缩为类似 `SZU-Badminton-Draw_v3.0.0_win-x64_self-contained.zip` 的文件，并随版本标签一起上传。Release 说明中应列出规则化抽签、赛程编排、多格式导出、带比赛时间和场地的对阵表、虚拟测试名单等重要变化。

## 示例名单

仓库的 `samples` 目录包含可直接导入软件的虚拟测试名单。它们不会参与自动化测试，但方便读者验证 GUI 流程：

- `深大羽协虚拟双打参赛名单_159人.xlsx`
- `深圳大学虚拟双打参赛名单_29人.xlsx`
- `深圳大学虚拟29学院参赛名单.xlsx`

## 跨平台说明

`BadmintonDraw.Core` 和 `BadmintonDraw.Excel` 是普通 .NET 项目，理论上可以在 macOS/Linux 上参与部分构建和测试。但当前 GUI 使用 WPF，只能在 Windows 上运行。

如果未来要支持 macOS/Linux，可以考虑：

- 保留 `BadmintonDraw.Core` 作为跨平台核心。
- 把 WPF 界面迁移或另做 Avalonia、MAUI、Web 前端。
- 保持 Excel、图片、PDF 导出逻辑在独立类库中，减少界面迁移成本。
