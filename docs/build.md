# Windows 构建与发布

## 开发机准备

安装：

- Visual Studio 2022
- `.NET 桌面开发` 工作负载
- .NET 8 SDK

## 本地运行

在仓库根目录执行：

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/BadmintonDraw.App
```

也可以直接用 Visual Studio 打开 `BadmintonDraw.sln`，将 `BadmintonDraw.App` 设为启动项目。

## 发布单文件程序

```powershell
dotnet publish src\BadmintonDraw.App\BadmintonDraw.App.csproj -c Release -r win-x64 --self-contained true --no-restore /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true
```

生成文件位于：

```text
src\BadmintonDraw.App\bin\Release\net8.0-windows\win-x64\publish
```

把发布目录中的程序发给干事即可使用。

正式发布到 GitHub Release 时，建议将发布目录压缩为类似 `SZU-Badminton-Draw_vX.Y.Z_win-x64_self-contained.zip` 的文件，并随版本标签一起上传。
