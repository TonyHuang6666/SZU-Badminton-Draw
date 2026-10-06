# 微信小程序开发

本仓库包含一个独立的原生微信小程序基础工程，目录为 `miniprogram/`。当前工程用于验证微信开发者工具、模拟器和真机预览链路，首页显示品牌化 Hello World。这个阶段没有后端，也不会读取桌面版的 `.szbd` 文件。

## 当前范围

- AppID：`wx5b0b9396b20e002e`
- 技术：WXML、WXSS、JavaScript
- 页面：`pages/index/index`
- 运行入口：`miniprogram/`
- 后端：没有后端，不需要启动 `dotnet run`

现有桌面版仍然由 `BadmintonDraw.sln` 管理。小程序目录与 `src/`、`tests/` 分开，后续接入服务时会通过 ASP.NET Core API 复用抽签和排程领域逻辑，而不是让小程序直接访问本地存档。

## 安装开发工具

1. 从[微信开发者工具官方页面](https://developers.weixin.qq.com/miniprogram/dev/devtools/download.html)下载 macOS Apple Silicon 版本并安装。
2. 使用拥有该 AppID 权限的微信扫码登录开发者工具。
3. 如果开发者工具提示基础库版本，选择稳定的调试基础库即可；Hello World 页面不依赖后端能力。

本机的 Node.js、npm 和 .NET SDK 只用于仓库检查及后续服务端开发，不替代微信开发者工具。开发者工具需要单独安装和登录，仓库不会自动安装它。

## 导入项目

1. 打开微信开发者工具，选择“本地小程序项目”或“导入项目”。
2. 项目目录选择仓库中的 `miniprogram/`，不要选择仓库根目录。
3. AppID 填写 `wx5b0b9396b20e002e`；项目配置文件中已经写入同一 AppID。
4. 导入后确认左侧页面树包含 `pages/index/index`，点击“编译”。

开发者工具可能为当前电脑生成 `project.private.config.json`。该文件只用于本机设置，已经被 Git 忽略，不要改名提交，也不要把 AppSecret、代码上传密钥或云环境凭据写入仓库。

## 运行与预览

- **模拟器**：点击“编译”后，在模拟器中应看到“深大羽协·赛事助手”和 `Hello World`。
- **真机预览**：点击工具栏“预览”，使用微信扫描二维码，在真机上查看同一首页。当前页面没有网络请求，所以真机预览不需要配置业务域名。
- **代码修改**：修改 `miniprogram/pages/index/` 下的 WXML、WXSS 或 JavaScript 后重新编译即可看到变化。
- **品牌资源**：首页使用 `miniprogram/assets/logo/szu-badminton-association.png`，资源是本地文件，不依赖外部 URL。

## 仓库侧验证

在仓库根目录执行：

```sh
node --test tools/miniprogram-structure.test.mjs
dotnet build BadmintonDraw.sln -c Release --no-restore
dotnet test BadmintonDraw.sln -c Release --no-build --verbosity normal
```

第一条命令检查 AppID、页面路径、资源和文档；后两条命令确认加入小程序目录后桌面版仍可构建和测试。

## 后续接入方向

第一阶段只验证客户端壳。后续会按独立阶段接入：

1. ASP.NET Core API 与微信身份校验。
2. 赛事列表、赛事概览和权限边界。
3. 名单上传与校验、公开抽签和审计结果。
4. 异步赛程编排任务、赛程板和选手兼项查看。
5. 现场材料下载、赛果录入和后续对阵刷新。

排程生成可能持续较长时间，正式 API 需要使用任务状态和取消机制；材料导出需要返回受权限保护的临时下载地址。桌面版和 `.szbd` 存档在迁移期间保持可独立使用。
