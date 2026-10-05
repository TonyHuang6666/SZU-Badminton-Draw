# 构建、验收与打包

本文提供当前仓库的开发、依赖审计、测试、端到端验收和本地打包入口。命令从仓库根目录执行；每一步确认退出码为零后，再继续下一步。构建结果应与当次源提交、工作区修改、运行环境和输出目录一起记录。已保存的验收记录见[文档归档](archive/index.md)，其结论仅适用于记录中的输入和运行。

## 开发环境与版本来源

| 项目 | 当前配置 |
| --- | --- |
| SDK | `global.json` 指定 `10.0.301`，`rollForward=latestPatch`，禁用预览版 |
| 目标框架 | `net10.0` |
| 产品版本 | `Directory.Build.props` 的 `VersionPrefix=5.0.0` |
| 桌面入口 | `src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj`，Avalonia |
| 发布 RID | `win-x64`、`osx-arm64`、`osx-x64` |
| 依赖锁定 | `RestorePackagesWithLockFile=true`，各工程提交 `packages.lock.json` |

`latestPatch` 允许使用同一 SDK 功能带中的较新补丁；运行前检查 `dotnet --version` 与 `dotnet --info`。SDK 的实际选择以仓库根目录的解析结果为准。可用 Visual Studio、Rider、VS Code 或终端打开 `BadmintonDraw.sln` 开发，启动项目选 Desktop。

主要依赖包括 Avalonia（桌面 UI）、ClosedXML（工作簿）、SkiaSharp（图片/PDF）、Microsoft.Data.Sqlite 与 SQLitePCLRaw（赛事存档），测试使用 xUnit 和 Avalonia Headless。依赖版本以各工程及锁文件为准；详细模块关系见[系统架构](architecture.md)。

macOS 打包需要 Bash、Python 3、Git、.NET SDK、系统 `hdiutil` 与 `codesign`；`sips`、`iconutil` 和图标源文件齐备时生成应用图标。打包安全测试要求 Python 3.9 或更高。Windows 的依赖审计命令需要 Bash，例如 Git for Windows 提供的 Bash；CI 已显式选择该 shell。

自包含发布包携带 .NET 运行时，普通用户运行应用无需另装 SDK 或 Python；应用生成工作簿、PDF 和图片也无需后台启动 Office。填写与重算 Excel 记录表需要相应办公软件。当前 CI 和打包目标覆盖 Windows 与 macOS；Linux 的原生运行和分发需另行验证。

## 锁定还原、构建与运行

```sh
dotnet --version
dotnet restore BadmintonDraw.sln --locked-mode -p:Configuration=Release
bash scripts/check-vulnerable-packages.sh BadmintonDraw.sln
dotnet build BadmintonDraw.sln -c Release --no-restore
dotnet test BadmintonDraw.sln -c Release --no-build --verbosity normal
dotnet run --project src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj -c Release --no-build
```

`--locked-mode` 要求依赖图与锁文件一致。桌面项目已声明三个发布 RID，锁定还原为这些发布目标准备依赖。依赖调整时显式刷新并评审锁文件，再执行完整验证链；发布使用锁定还原和 `--no-restore`。

依赖审计脚本执行：

```sh
dotnet list BadmintonDraw.sln package --vulnerable --include-transitive --format json --output-version 1
```

脚本保留 JSON 输出，命令失败或报告中出现 `advisoryurl` 时退出失败。应核对完整输出及退出码；查询错误、服务不可用和缺失审计证据需作为异常处理。漏洞结论只反映执行当时的查询结果，关闭审计或忽略还原错误会使发布证据不完整。

日常调试可使用：

```sh
dotnet run --project src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj
```

该命令使用默认 Debug 配置并可触发还原、构建。使用 `--no-build` 前需完成相同配置的构建；修改源码后应重新构建，再运行验证。

## 测试入口与覆盖范围

需要单独验证某一层时，在上述 Release 构建后执行：

```sh
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj -c Release --no-build --verbosity normal
dotnet test tests/BadmintonDraw.Desktop.Tests/BadmintonDraw.Desktop.Tests.csproj -c Release --no-build --verbosity normal
```

| 测试范围 | 主要检查内容 |
| --- | --- |
| 抽签与身份 | 单打、双打、团体名单；种子与轮空；淘汰赛和循环赛；可复现抽签；类型化前后依赖 |
| 全赛事排程 | 日期、场地、裁判容量、不可用时段、依赖、兼项、休息和每日上限；有界搜索、取消、容量证据与质量结果 |
| 存储与工作流 | schema、真实 SQLite 重开、修订冲突、候选保存、备份、恢复、只读检查和故障后的真实提交状态 |
| 现场材料 | 抽签及运营材料、记录表字面身份与公式、跨表依赖、整批导入、更正、重复文件、部分导出与覆盖冲突 |
| 桌面 | 实际 Avalonia 控件与绑定、导航、窗口生命周期、保存状态、确认失效和迟到回调 |

Headless 通过可说明相应控件与逻辑测试成功；原生窗口、文件选择器、另一个操作系统和打印机仍需各自的运行证据。测试总数随覆盖变化，以目标源码的一次实际输出为准。

GUI 手工练习可从[样例名单](../samples/v5/README.md)选择场景，对照[使用说明](usage.md)执行。样例是虚拟数据，运行练习应使用新建赛事文件。

## 有界端到端验收

验收入口位于 `tools/BadmintonDraw.Acceptance`。完成 Release 构建后可运行：

```sh
dotnet run --project tools/BadmintonDraw.Acceptance/BadmintonDraw.Acceptance.csproj -c Release --no-build --
```

省略参数时，工具自动创建：

```text
artifacts/acceptance/v5.0.0/run-<UTC时间>-<GUID>/
```

如需明确指定目录：

```sh
dotnet run --project tools/BadmintonDraw.Acceptance/BadmintonDraw.Acceptance.csproj -c Release --no-build -- --output artifacts/acceptance/v5.0.0/local-review-01
```

目标必须是全新或空目录，再次运行使用不同目录。工具拒绝仓库根目录、受保护的源码/样例目录和不允许的符号链接路径，保留已产生的证据。

入口先记录 Git 提交、工作区状态、运行时和平台，再执行直接与传递依赖审计。七个必需场景分别在有超时上限的子进程内执行：

| 场景目录 | 范围 | 子进程超时 |
| --- | --- | --- |
| `01-public-draw` | 仅公开抽签流程 | 10 分钟 |
| `02-single` | 单项目赛事生命周期 | 10 分钟 |
| `03-multiple` | 多项目统一赛事生命周期 | 10 分钟 |
| `04-team` | 团体赛事生命周期 | 10 分钟 |
| `05-faults` | 隔离副本上的故障与恢复 | 10 分钟 |
| `06-large-292` | 292 场规模场景 | 4 分钟 |
| `07-rejection-345` | 345 场安全拒绝场景 | 3 分钟 |

工具通过真实工作流新建赛事、导入虚拟名单、明确抽签与确认、生成赛程、导出材料、填写材料副本、导入赛果、重开及恢复。各场景必须达到自己的完成条件；缺失结果、子进程异常、超时和未达到要求均算失败。

证据目录包含 `source-environment.json`、依赖审计输出、各场景 `*-process.json`、逐步记录、`acceptance-results.json`、`artifact-inventory.json` 和 `evidence-sha256.txt`，以及对应存档、原始材料与填写副本。完整通过需同时核对主进程退出码、总报告 `Success`、七个场景结果及证据清单。工具不要求工作区必须干净，因此正式候选还需单独核对来源状态。

托管材料读回和静态公式检查验证文件结构、来源身份与公式绑定。报告中的 `Office`、`PdfVisual`、`Windows`、`Native`、`PhysicalPrinting` 默认记录为 `NotRun`；这些项目需要另附实际运行证据。原始导出与填写副本分别保留哈希，办公软件重算也在副本上进行。

## 指定存档的排程审计

验收工具还提供本地排程诊断入口：

```sh
dotnet run --project tools/BadmintonDraw.Acceptance/BadmintonDraw.Acceptance.csproj -c Release --no-build -- --scheduling-audit /absolute/path/tournament.szbd --output artifacts/scheduling-audit/local-review-01
```

输入赛事的每个项目都需要已有比赛图，输出必须为全新或空目录。工具先复制输入并校验 SHA-256，在副本上读取比赛图；运行结束再次核对原文件哈希。原始输入副本与诊断数据仅写入所选输出目录，分享证据前应检查其中的参赛者信息。

该入口使用源码内定义的资源场景：2026-10-03 至 10-06、每日 14:00–18:00、首日 24 场地/其余 16 场地、30 分钟时长，并分别考察休息 30 分钟且每日上限 4 场、48 场地、休息 0 分钟且每日上限 8 场等条件；另验证随工具提供的 292 场完整候选和两种策略。它读取输入的比赛图，资源和策略取自这些审计场景。

每个场景写出请求、结果、诊断与摘要；生成成功时另在隔离进程中运行完整校验器。总结果为 `audit-results.json`。审计运行成功表示场景执行与结果检查达到工具要求，各场景实际属于生成成功、容量拒绝或搜索未完成，应继续查看其 `Status` 和独立校验结果。

## macOS 本地打包

```sh
python3 scripts/test_packaging_preflight.py
bash scripts/publish-macos.sh osx-arm64
```

第一条使用隔离的模拟工具检查参数、路径、版本、签名失败等安全边界；真实 DMG 由第二条生成和验证。支持 `osx-arm64`、`osx-x64`，省略参数默认 `osx-arm64`。脚本内部执行桌面项目的锁定还原和自包含发布，完整测试及依赖审计仍按前文单独执行。

版本通过 `packaging_metadata.py` 调用 MSBuild 读取实际 `VersionPrefix`。可直接核对：

```sh
python3 scripts/packaging_metadata.py version src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj Release
```

可设置的环境变量如下：

| 变量 | 默认值与规则 |
| --- | --- |
| `VERSION` | 读取项目版本；显式值须为三段数字，无前后缀与空白，每段 0–65534 |
| `CONFIGURATION` | `Release`，支持 `Release` 或 `Debug` |
| `APP_NAME` | `SZU Badminton Draw`，须通过文件名校验 |
| `BUNDLE_ID` | `com.szuba.badmintondraw`，须通过点分标识校验 |
| `CODESIGN_IDENTITY` | `-`，表示 ad-hoc 签名；其他非空值作为签名身份 |

输出结构为：

```text
artifacts/macos/<RID>/<version>/run-<GUID>/
  publish/
  dmg-root/
    <APP_NAME>.app/
      Contents/Info.plist
      Contents/MacOS/
      Contents/Resources/build-metadata.json
    Applications -> /Applications
  SZU-Badminton-Draw_<version>_<RID>.dmg
```

每次原子创建独立运行目录，保留既有产物和失败现场；固定输出父目录必须为真实目录。版本值同时传给程序集发布和 Info.plist；后者声明最低系统版本 12.0，实际系统可用性仍需启动测试确认。

`build-metadata.json` 保存版本来源、完整源提交、dirty 标记、RID、配置与预检时间，描述打包预检时的工作树状态。正式候选应冻结输入，核对元数据、程序集版本、可执行架构和最终哈希；构建期间继续编辑源码会破坏这种对应关系。

脚本对完整应用包签名后执行 `codesign --verify --deep --strict`，再创建 DMG 并执行 `hdiutil verify`。失败即使留下 DMG，也需按失败产物处理。指定非空签名身份时附加 hardened runtime 与时间戳；脚本的分发流程止于签名和 DMG 校验，公证与 stapling 需另外完成。公开分发还应检查实际 Gatekeeper 体验。

## Windows 本地发布

先在 Windows 完成前文的 Release 锁定还原、审计、构建与测试，再在仓库根目录的 PowerShell 执行：

```powershell
$packageVersion = python scripts/packaging_metadata.py version src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj Release
if ($LASTEXITCODE -ne 0) { throw "无法读取版本" }
$packageRun = [guid]::NewGuid().ToString("N")
$packageDirectory = "artifacts/windows/win-x64/$packageVersion/run-$packageRun"
dotnet publish src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true "-p:Version=$packageVersion" "-p:VersionPrefix=$packageVersion" -o $packageDirectory
if ($LASTEXITCODE -ne 0) { throw "Windows 发布失败，保留本次输出检查" }
Get-ChildItem $packageDirectory
Get-FileHash "$packageDirectory/BadmintonDraw.Desktop.exe" -Algorithm SHA256
```

输出位于 `artifacts/windows/win-x64/<version>/run-<GUID>/`。核对真实 PE 架构、程序集/文件版本、源提交记录与 SHA-256，并在 Windows 启动，完成存档打开、文件选择和现场操作。Windows 命令没有生成 macOS 包中的 `build-metadata.json`，源提交与工作树状态需另外留存。

单文件发布参数会嵌入并配置自解压的原生依赖；实际输出仍应逐项检查。存在必要附属文件时，按验证结果一起交付。其他平台上交叉发布生成 Windows 资产后，仍需 Windows 原生运行验收。

## CI 与交付证据

[`.github/workflows/ci.yml`](../.github/workflows/ci.yml) 在 `main` 推送和 Pull Request 时触发，两个任务均执行锁定还原、直接/传递依赖审计、Release 构建、测试和实际发布：

| CI 任务 | 执行环境 | 发布与上传 |
| --- | --- | --- |
| Windows | `windows-latest` | 自包含 `win-x64`；上传 `artifacts/windows/win-x64/<version>/`，artifact 名称为 `SZU-Badminton-Draw_<version>_win-x64` |
| macOS | `macos-14` | 安全脚本测试及真实 `osx-arm64` DMG 打包、验证；上传本次版本下运行目录中的 DMG，artifact 名称为 `SZU-Badminton-Draw_<version>_osx-arm64` |

CI 使用 `actions/setup-dotnet` 安装 `10.0.x`，实际 SDK 仍受 `global.json` 约束。每个任务读取一次实际版本并传给发布命令；上传路径无文件时任务失败。当前 CI 没有调用完整七场景验收入口，也没有自动执行 Office 重算、原生 GUI 流程、物理打印、公证或 GitHub Release 发布；这些检查按交付需要另行记录。

一份可复核的交付记录应包含：

1. 源提交、工作树状态、SDK、操作系统、架构、配置，以及完整命令和退出码。
2. 锁文件、漏洞审计、测试及七个必需验收场景的实际输出，包含失败记录和修复后的复测。
3. 原始导出与填写/办公软件副本的哈希、公式重算、逐页视觉检查范围和未执行项。
4. 实际交付平台的启动、文件选择、赛事操作和材料打印检查。
5. 安装包路径、程序集/包版本、来源核对、SHA-256 和签名/公证状态。

CI artifact、本地打包目录和正式发布资产应分别标明来源。上传或发布时选用本次验证对应的文件，并逐项校对哈希。耗时必须同时注明输入规模、资源、平台、源版本和结果；已有报告中的数字保留其原始运行含义。

## 文档维护

用户操作变化同步更新 `README.md`、`docs/usage.md` 和 `docs/troubleshooting.md`；领域模型、存储和模块边界变化更新 `docs/architecture.md` 与 `docs/algorithm.md`；排程约束和策略变化更新 `docs/scheduling.md`、`docs/fairness.md` 和 `docs/rules-compliance.md`。

测试数量、耗时、哈希、产物路径和平台结论应来自明确记录的执行。设计稿与冻结验收证据在[归档](archive/index.md)中保留状态和上下文，当前使用与工程文档按已实现的行为维护。
