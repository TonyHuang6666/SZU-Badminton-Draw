# Release 4.0 核查清单

本文记录 4.0 发布后应保留的核查信息，方便后续追溯发布包、CI 状态和已知风险。

## 发布信息

- Release: <https://github.com/TonyHuang6666/SZU-Badminton-Draw/releases/tag/v4.0.0>
- 主发布提交：`2701cd1`
- README 4.0 文档补充提交：`77f73ba`
- CI：<https://github.com/TonyHuang6666/SZU-Badminton-Draw/actions/runs/27478861515>
- CI 结论：macOS 与 Windows 构建任务通过。

## 发布资产

| 文件 | 用途 | SHA-256 | 大小 |
| --- | --- | --- | ---: |
| `SZU-Badminton-Draw_Avalonia_macOS_osx-arm64_v4.0.0.dmg` | macOS Avalonia 版 | `f040e1256fb188cfb35ad4bb9cd7cb4e1c6f04c097179395dcd44c7ecdc63617` | 63,923,354 bytes |
| `SZU-Badminton-Draw_Avalonia_Windows_win-x64_v4.0.0.exe` | Windows Avalonia 版 | `c97c0e6c06df801f857ea5a47db00fea6d8cfb65cf0ddee53e57fa983a2bc59d` | 58,902,339 bytes |
| `SZU-Badminton-Draw_WPF_Windows_win-x64_v4.0.0.exe` | Windows WPF 备用版 | `783db9402eab23ed0ad09b06fbec1f3bd890b9822736433c5546732cd2f978c7` | 88,718,637 bytes |

## 已核查项目

- `dotnet test` 覆盖抽签、赛程、记录表导入、赛事存档、多项目冲突、合并材料包和视觉导出关键路径。
- Avalonia macOS 版可以生成 `.app` 与 `.dmg`。
- macOS DMG 使用 `hdiutil verify` 验证通过。
- Windows PDF 中文方格问题通过内置 Noto CJK 字体修复。
- 合并材料包导出前会创建独立文件夹，避免多个比赛日文件散落在导出目录。
- 合并赛程记录表中的跨项目胜者/负者引用会带上项目名前缀，避免不同项目同名场次互相串联。

## 已知风险

- macOS 包仍未 Developer ID 签名、公证和 staple，适合内部测试，不适合直接对外公开分发。
- WPF 版保留为 Windows 备用版，但后续新功能应优先沉淀到 `Core`、`Excel`、`Workflows` 和 Avalonia。
- 4.0 的多项目自动调整是保守策略：只移动未完成且存在冲突的比赛，用于辅助裁判长找空位，不等同于正式赛程审批。
- 正式规则中的休息间隔和每日/每单元场数限制在 4.0 中以可配置约束和提醒为主，深大校内赛事仍需要裁判长结合场馆现实确认。

## 下次发布前必跑

```bash
dotnet restore
dotnet build BadmintonDraw.sln --no-restore --verbosity minimal
dotnet test tests/BadmintonDraw.Tests/BadmintonDraw.Tests.csproj --no-build --verbosity minimal
VERSION=x.y.z bash scripts/publish-macos.sh osx-arm64
```

Windows 发布还应在 Windows 机器上分别发布 Avalonia 与 WPF 单文件程序，并至少验证：

- 启动程序。
- 生成抽签结果。
- 生成赛程预览并打开时间场地窗口。
- 导出首日材料包。
- 加载两个 `.szbd` 进入多项目赛程编排，导出合并材料包。
