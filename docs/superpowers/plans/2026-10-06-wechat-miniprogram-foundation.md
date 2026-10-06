# 微信小程序基础工程实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在现有赛事工作台仓库中新增一个可由微信开发者工具直接导入、无需后端即可运行的原生微信小程序 Hello World 基础工程。

**Architecture:** 小程序作为独立 `miniprogram/` 客户端，第一阶段只包含品牌化首页和开发配置，不触碰桌面版、`.szbd` 存档或排程算法。后续通过 ASP.NET Core API 复用 `BadmintonDraw.Core` 与 `BadmintonDraw.Workflows`；小程序不直接访问本地文件或 SQLite。

**Tech Stack:** 微信原生小程序（WXML、WXSS、JavaScript）、Node.js 22 内置测试运行器、现有 .NET 10/Avalonia 解决方案。

**Spec:** `docs/superpowers/specs/2026-10-06-wechat-miniprogram-foundation-design.md`

## Global Constraints

- AppID 固定为 `wx5b0b9396b20e002e`；不得写入 AppSecret、代码上传密钥、云环境密钥或个人机器路径。
- 小程序目录必须独立于 `src/` 和 `tests/`，不修改 `.szbd` schema、桌面导航或现有领域逻辑。
- 第一阶段只使用原生 WXML、WXSS 和 JavaScript，不引入 uni-app、Taro、React、Vue 或运行时依赖。
- 页面使用紫红品牌色和仓库内获准使用的高清 logo；首页只展示基础工程状态，不伪造已接通的赛事数据。
- `project.private.config.json` 必须被 Git 忽略；可提交配置只能包含非机密项目设置。
- 完成后必须验证 Node 静态检查、现有 .NET Release 构建和相关测试。

## Review Focus

- 开发者工具导入根目录是否误选为仓库根目录，导致页面路径或 `miniprogramRoot` 错误；由 Task 1 的配置测试固定。
- AppID 或 JSON 配置损坏时是否能在导入前被发现；由 Task 1 的结构与 JSON 断言固定。
- 首页引用 logo 时资源路径大小写或文件名不一致；由 Task 2 的资源路径测试固定。
- 首页是否误加入尚未实现的登录或赛事按钮；由 Task 2 的页面内容断言固定。
- 添加小程序目录后桌面解决方案是否仍能构建；由 Task 3 的完整 Release 构建验证固定。

---

### Task 1: 创建微信小程序工程骨架与安全配置

**Files:**
- Create: `miniprogram/project.config.json`
- Create: `miniprogram/project.private.config.json.example`
- Create: `miniprogram/app.json`
- Create: `miniprogram/app.js`
- Create: `miniprogram/app.wxss`
- Modify: `.gitignore`
- Test: `tools/miniprogram-structure.test.mjs`

**Interfaces:**
- Consumes: AppID `wx5b0b9396b20e002e` and the directory contract in the spec.
- Produces: A project that the WeChat Developer Tools can import from `miniprogram/`, with `pages/index/index` as its only page and no product dependencies.

- [ ] **Step 1: Write the failing structure test**

  In `tools/miniprogram-structure.test.mjs`, use Node's `node:test`, `assert/strict`, `fs`, and `path` to assert that:
  - `miniprogram/project.config.json` parses and has `appid === "wx5b0b9396b20e002e"`, `compileType === "miniprogram"`, and `miniprogramRoot === "./"`.
  - `miniprogram/app.json` parses and has exactly one page, `pages/index/index`, and a non-empty `window.navigationBarTitleText`.
  - `miniprogram/app.js` and `miniprogram/app.wxss` exist.
  - `miniprogram/project.private.config.json` is absent from tracked files and `.gitignore` contains its pattern.

- [ ] **Step 2: Run the structure test to verify it fails**

  Run: `node --test tools/miniprogram-structure.test.mjs`

  Expected: FAIL because the `miniprogram/` files do not exist yet.

- [ ] **Step 3: Implement the project configuration**

  Create `project.config.json` with the fixed AppID, `compileType: "miniprogram"`, `miniprogramRoot: "./"`, project name `szu-badminton-draw-miniprogram`, and development settings for ES6, PostCSS, enhanced compilation, hot reload, and URL checking. Create `app.json` with one index page and a neutral light navigation bar. Create `app.js` with only the application lifecycle and `globalData.appName` / `globalData.phase` values. Create `app.wxss` with the shared purple brand tokens and basic page reset styles. Add `miniprogram/project.private.config.json` to `.gitignore`, and add the example file with placeholder-only local path settings.

- [ ] **Step 4: Run the structure test to verify it passes**

  Run: `node --test tools/miniprogram-structure.test.mjs`

  Expected: PASS with all structure assertions green.

- [ ] **Step 5: Commit**

  ```bash
  git add .gitignore miniprogram tools/miniprogram-structure.test.mjs
  git commit -m "feat: scaffold WeChat mini program project"
  ```

### Task 2: Add the branded Hello World home page

**Files:**
- Create: `miniprogram/pages/index/index.js`
- Create: `miniprogram/pages/index/index.json`
- Create: `miniprogram/pages/index/index.wxml`
- Create: `miniprogram/pages/index/index.wxss`
- Create: `miniprogram/assets/logo/szu-badminton-association.png`
- Modify: `tools/miniprogram-structure.test.mjs`

**Interfaces:**
- Consumes: The `app.json` page entry and brand tokens from Task 1.
- Produces: A single self-contained home screen with a local logo, product title, Hello World message, and explicit “基础工程” status.

- [ ] **Step 1: Extend the failing test with page and asset assertions**

  Assert that the four index page files exist, `index.json` parses, `index.wxml` contains `深大羽协·赛事助手` and `Hello World`, `index.wxss` contains the agreed brand color token, and `miniprogram/assets/logo/szu-badminton-association.png` exists with a non-zero size. Assert that the WXML does not contain placeholder login, upload, or scheduling actions.

- [ ] **Step 2: Run the extended test to verify it fails**

  Run: `node --test tools/miniprogram-structure.test.mjs`

  Expected: FAIL because the page files and logo have not been added.

- [ ] **Step 3: Implement the page and copy the approved logo**

  Create `index.js` with static data for the title, subtitle, greeting, and stage label; do not call network or login APIs. Create `index.json` with the page navigation title. Create `index.wxml` as a centered responsive card: local logo, title “深大羽协·赛事助手”, subtitle “BADMINTON / TOURNAMENT STUDIO”, `Hello World`, and “小程序基础工程 · 后续接入赛事工作流” status text. Create `index.wxss` using the Task 1 purple palette, accessible text contrast, safe-area padding, and no unimplemented buttons. Copy `/Users/tony_huang/Library/CloudStorage/OneDrive-email.szu.edu.cn/深大羽协/公共文件/深大羽协红logo.png` to the exact asset path, preserving the source image without embedding an external URL.

- [ ] **Step 4: Run the page test to verify it passes**

  Run: `node --test tools/miniprogram-structure.test.mjs`

  Expected: PASS, including page text, resource path, and no-placeholder assertions.

- [ ] **Step 5: Commit**

  ```bash
  git add miniprogram/pages miniprogram/assets/logo tools/miniprogram-structure.test.mjs
  git commit -m "feat: add branded mini program hello world page"
  ```

### Task 3: Document local development and protect the desktop build boundary

**Files:**
- Create: `docs/wechat-miniprogram.md`
- Modify: `docs/index.md`
- Modify: `README.md`
- Test: `tools/miniprogram-structure.test.mjs`

**Interfaces:**
- Consumes: The importable project from Tasks 1–2 and the current repository commands in `docs/build.md`.
- Produces: A user-facing setup guide and a static verification that the guide points to the correct project directory and does not expose secrets.

- [ ] **Step 1: Add failing documentation assertions**

  Extend the Node test to assert that `docs/wechat-miniprogram.md` exists and mentions `miniprogram/`, the AppID, the official developer tool download URL, import steps, simulator/real-device preview, and the fact that the first phase has no backend. Assert that the document does not contain `AppSecret` values or private-key material. Assert that `docs/index.md` and `README.md` link to the new guide.

- [ ] **Step 2: Run the test to verify it fails**

  Run: `node --test tools/miniprogram-structure.test.mjs`

  Expected: FAIL because the guide and links do not exist.

- [ ] **Step 3: Write the development guide and navigation links**

  Document macOS Apple Silicon setup, the official developer tool download page, importing `miniprogram/` as the project root, using AppID `wx5b0b9396b20e002e`, compiling the Hello World page, simulator preview, real-device preview, and the boundary between this prototype and the future ASP.NET Core API. Add a “微信小程序” entry to `docs/index.md` and a concise link in the README development section. Do not claim that the developer tool is installed automatically.

- [ ] **Step 4: Run the static checks and the desktop regression build**

  Run: `node --test tools/miniprogram-structure.test.mjs`

  Expected: PASS with all mini program, asset, and documentation assertions green.

  Then run: `dotnet build BadmintonDraw.sln -c Release --no-restore`

  Expected: PASS; adding `miniprogram/` does not change the desktop build.

- [ ] **Step 5: Commit**

  ```bash
  git add docs/wechat-miniprogram.md docs/index.md README.md tools/miniprogram-structure.test.mjs
  git commit -m "docs: document WeChat mini program setup"
  ```

### Task 4: Final verification and handoff

**Files:**
- Test: `tools/miniprogram-structure.test.mjs`
- Verify: `BadmintonDraw.sln`

**Interfaces:**
- Consumes: All committed files from Tasks 1–3.
- Produces: Evidence that the mini program structure and existing desktop solution both remain healthy.

- [ ] **Step 1: Run the complete first-phase verification**

  Run:

  ```bash
  node --test tools/miniprogram-structure.test.mjs
  dotnet build BadmintonDraw.sln -c Release --no-restore
  dotnet test BadmintonDraw.sln -c Release --no-build --verbosity normal
  git status --short
  ```

  Expected: Node tests pass, the Release build passes, existing .NET tests pass, and Git status contains no private config, secret, or generated build files.

- [ ] **Step 2: Record the handoff**

  Report the exact import directory (`miniprogram/`), the verified AppID, the commands and results, and the remaining manual step of opening the project in the installed WeChat Developer Tools. Do not present backend migration as complete.
