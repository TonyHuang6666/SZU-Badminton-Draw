# 5.0 构建、验收与发布准备

5.0 在独立开发分支实施；本说明中的打包命令只生成本地产物，不推送代码、创建标签或发布 GitHub Release。当前门禁状态见[开发进度](superpowers/plans/2026-09-13-v5-progress.md)，历史版本验收不能替代当前结果。

## 开发环境

- 使用仓库 `global.json` 指定的 .NET 10 SDK；从仓库根目录运行命令，确保 SDK 选择规则生效。
- 桌面入口只有 `src/BadmintonDraw.Desktop`（Avalonia）。使用支持该 SDK 的编辑器或 IDE。
- macOS 打包另外需要 Bash、Python 3 标准库、Git 和系统 `hdiutil`；系统 `sips` / `iconutil` 可用且图标存在时会生成应用图标。安全测试需要 Python 3.9 或更高版本。
- 普通用户运行自包含应用不需要安装 Python、.NET SDK 或 Office。若要填写、重算 Excel 记录表，需要另备办公软件；应用自身的导出不依赖后台启动 Office。
- Windows 与 macOS 是本次交付目标；Linux 未纳入最终运行验收，不能仅凭跨平台框架推断已经支持所有桌面环境。

项目依赖由 NuGet 和各工程的 `packages.lock.json` 管理。不要用关闭漏洞审计、忽略还原失败或跳过测试来让流水线变绿。

## 本地构建与测试

从仓库根目录执行：

```sh
dotnet restore BadmintonDraw.sln --locked-mode -p:Configuration=Release
bash scripts/check-vulnerable-packages.sh BadmintonDraw.sln
dotnet build BadmintonDraw.sln -c Release --no-restore
dotnet test BadmintonDraw.sln -c Release --no-build
dotnet run --project src/BadmintonDraw.Desktop -c Release --no-build
```

先构建再使用 `--no-build`；修改代码后不要用旧输出验证新源码。跨 RID 发布需要相应运行时还原，不能假设普通本机还原已经包含 Windows/macOS 发布资产。

共享测试覆盖类型化比赛图、统一硬约束、真实 SQLite、记录表读写、备份和故障；桌面测试覆盖实际 Avalonia 控件、导航、确认失效与迟到回调。Headless 通过不等于真实窗口、文件选择器、打印或另一个操作系统通过。

依赖审计包含直接和传递包。还原锁文件与漏洞查询服务不可用属于门禁异常，应保留错误，不标记为“没有漏洞”。审计结果只反映执行当时的数据。

## 有界端到端验收

正式验收工具位于 `tools/BadmintonDraw.Acceptance`，5.0 版本的入口为：

```sh
dotnet run --project tools/BadmintonDraw.Acceptance -c Release -- --output artifacts/acceptance/v5.0.0/local-review-01
```

使用全新的、专门用于本次验收的输出目录；再次运行换一个目录，不删除上次证据。工具使用真实工作流创建赛事、导入虚拟名单、显式抽签确认、生成全局赛程、导出实际材料、填写副本、导入结果、重开和恢复，并运行独立的大规模成功/安全拒绝场景。

每个必需场景在有界子进程执行，保留步骤、耗时、标准输出/错误、退出状态、存档和产物清单；缺失或失败场景不能算通过。故障注入只作用于隔离副本。原始导出与填写副本分别保留哈希，不在正常验收运行中改写仓库样例。

具体场景、实际规模和本次报告路径以验收报告为准。工具里的 managed 文件读回与静态公式检查不能替代 LibreOffice/Microsoft Excel 真正重算；实际视觉检查、原生桌面、Windows 和物理打印未做时应标记未执行。

## macOS 本地打包

打包脚本的版本默认来自实际 MSBuild `VersionPrefix`，不是文件名猜测或另一个硬编码常量。

```sh
python3 scripts/test_packaging_preflight.py
bash scripts/publish-macos.sh osx-arm64
```

第一条是使用隔离假发布工具的安全回归，不是真实 DMG 验证。第二条才执行真实自包含发布、组装 `.app`、生成和校验 DMG。支持 `osx-arm64` 与 `osx-x64`；发布出另一种架构不表示已经在该架构上启动测试。

实际输出路径由脚本打印，结构为：

```text
artifacts/macos/<RID>/<version>/run-<unique>/
  publish/
  dmg-root/<APP_NAME>.app/
  SZU-Badminton-Draw_<version>_<RID>.dmg
```

每次运行创建新目录，保留旧产物；不再递归清理输出根目录。固定输出父目录有符号链接或不是实际目录时拒绝。不要通过自建软链接把发布目录指向其他资料。

可显式设置 `VERSION=x.y.z`，但必须是无前缀、无后缀的三段数字；同一值传给程序集发布和 Info.plist。`CONFIGURATION` 支持 Release/Debug；应用名称与 bundle ID 也会预检，非法值在输出创建前拒绝。

应用内 `Contents/Resources/build-metadata.json` 保存版本来源、完整 Git 提交、dirty 标记、RID、配置和预检时间。它描述开始打包时的工作树，不是数字签名，也不能证明构建期间无人修改源码。正式验收应先冻结源码，并确认元数据、程序集版本、实际可执行架构和最终文件哈希相互对应。

脚本失败会保留本次目录；即使已有 DMG 文件，也必须确认真实 `hdiutil verify` 成功。当前脚本不进行 Developer ID 签名、公证或 stapling，不宣称通过 Gatekeeper 或已经完成公开分发验收。

## Windows 本地发布

在 Windows PowerShell、仓库根目录执行；Python 仅用于构建时读取并校验版本：

```powershell
$packageVersion = python scripts/packaging_metadata.py version src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj Release
if ($LASTEXITCODE -ne 0) { throw "无法读取版本" }
$packageRun = [guid]::NewGuid().ToString("N")
$packageDirectory = "artifacts/windows/win-x64/$packageVersion/run-$packageRun"
dotnet publish src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true "-p:Version=$packageVersion" "-p:VersionPrefix=$packageVersion" -o $packageDirectory
if ($LASTEXITCODE -ne 0) { throw "Windows 发布失败，保留本次输出检查" }
```

检查输出中的真实 PE 架构、程序集/文件版本、源提交信息和 SHA-256，并在实际 Windows 环境启动、打开存档、使用文件选择器与完成现场操作。macOS 上交叉发布成功仅证明可以生成 Windows 资产，不能代替这些运行检查。

不要只上传某个旧目录里碰巧存在的 `.exe`；必须绑定本次构建输入与实际输出。若发布目录包含必要的附属文件，应按实际验证结果交付，不能只凭“单文件”参数假定任何文件都可删除。

## CI 与正式交付门禁

现有 Windows、macOS 两个任务均执行锁定还原、依赖审计、构建、测试及实际发布。两者分别读取一次实际版本，用于发布参数和带版本的 artifact 名称；缺少输出即失败。macOS 另运行独立的脚本安全测试，并实际生成、校验 DMG。

CI 上传 artifact 不等于发布 Release。5.0 最终交付前应有：

1. 冻结的源提交与干净状态，完整测试、锁文件和漏洞审计记录。
2. 五条真实生命周期/故障流程及两个规模场景的验收结果，保留失败与修复复测证据。
3. 原始材料与填写/办公软件副本的哈希、公式重算、逐页视觉检查范围和明确限制。
4. Windows、macOS 的真实构建与运行结果；未执行平台不得写成通过。
5. 真实安装包、版本和来源核对、SHA-256 清单及清楚的未签名/未公证说明。
6. 用户审阅验收报告后，再按授权进行推送、远端 CI、合并、标签和正式发布。

任何耗时数字都应注明输入规模、资源、平台、源版本与失败/成功状态；测试数变化应说明新增或等价替换的覆盖，而不是只追求总数。
