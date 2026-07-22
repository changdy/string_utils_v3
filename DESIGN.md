# 字符串工具 V2 — Avalonia UI 迁移设计方案

## 1. 背景与目标

将现有 Electron 托盘工具（`str_toolkit`）的系统架构切换为 **Avalonia UI + .NET 8**，
以原生方式支持 Windows / macOS / Linux 多桌面平台发布，摆脱 Electron 运行时体积与内存开销。

核心功能保持对齐：

- 托盘常驻 + 全局快捷键（默认 `Ctrl+Alt+D`）唤醒无边框浮窗
- 唤醒后自动读取剪贴板，内置处理器按 `check()` 匹配分数自动选中
- `Enter` / 点击按钮执行 `transfer()`，结果写回编辑区并复制到剪贴板
- 处理器 `nextStep` 链式流转（如 提取 → 去重 → 拼接）
- 托盘菜单：功能显隐、开机自启、修改快捷键、打开用户脚本目录、退出
- 用户脚本扩展（沿用 Electron 版的 `solver` JS 契约）
- JSON 预览（jsoncrack / jsonhero 本地服务 + 系统浏览器）
- JSON Diff（调用 VSCode `code --diff`）
- 单实例锁

UI 不要求像素级复刻，整体布局与配色相似即可。
与 Electron 版的行为差异统一记录在 [DIFFERENCES.md](DIFFERENCES.md)。

## 2. 已确认的关键决策（grill-me 会话结论）

| # | 决策点 | 结论 |
|---|--------|------|
| 1 | 处理器与用户脚本语言 | **内置处理器重写为 C#；用 Jint 内嵌 JS 引擎兼容现有用户脚本** |
| 2 | JSON 预览 | **应用内嵌 Kestrel 托管静态资源 + 系统浏览器打开**（沿用原方案） |
| 3 | 全局快捷键 | **SharpHook**（libuiohook 封装）跨平台监听；Wayland 受限为已知限制 |
| 4 | UI 架构 | **Avalonia 11 + CommunityToolkit.Mvvm + .NET 8 LTS** |
| 5 | 功能范围 | **一次性完整对齐** Electron 版功能 |

## 3. 技术栈

| 组件 | 选型 | 对应 Electron 版 |
|------|------|------------------|
| UI 框架 | Avalonia 11.3 (FluentTheme, Light) | Electron + HTML/CSS |
| MVVM | CommunityToolkit.Mvvm 8.x | 无（原生 DOM 操作） |
| 运行时 | .NET 8 LTS | Node.js / Chromium |
| 全局快捷键 | SharpHook 5.x | `globalShortcut` |
| 托盘 | Avalonia `TrayIcon` + `NativeMenu` | `Tray` / `Menu` |
| 用户脚本 | Jint 4.x（ES Module 支持） | 渲染进程动态 `import()` |
| JSON 预览服务 | ASP.NET Core Kestrel（FrameworkReference） | express |
| SVG 图标 | Avalonia.Svg.Skia | `<img src=*.svg>` |
| 配置持久化 | JSON 文件（`SettingsService`） | electron-store |
| 大整数 JSON | `JsonDocument` + `GetRawText()` | json-bigint |
| 数值排序精度 | `decimal` | bignumber.js |
| 单实例 | 命名 Mutex + 命名管道唤醒 | `requestSingleInstanceLock` |
| 开机自启 | 注册表 / LaunchAgents / autostart .desktop | `setLoginItemSettings` |

## 4. 项目结构

```text
avalonia_ui/
  DESIGN.md                  本设计方案
  DIFFERENCES.md             与 Electron 版的行为差异记录
  StrToolkit.sln
  src/StrToolkit/
    Program.cs               入口：单实例锁 + AppBuilder
    App.axaml(.cs)           应用生命周期：服务装配、托盘、全局快捷键
    Views/
      MainWindow.axaml(.cs)  无边框浮窗：文本区 + 右侧功能图标栏 + Enter 按钮
    ViewModels/
      MainWindowViewModel.cs 打分选择、执行流程、快捷键修改模式
      SolverItemViewModel.cs 单个处理器项（图标/选中/显隐）
    Solvers/                 处理器（对应 src/script/texthandler/*.js）
      ISolver.cs             Check/Transfer 契约
      IdJoinSolver.cs        ID拼接
      SortDistinctSolver.cs  排序&去重
      NamingConversionSolver.cs 命名规则转换
      SqlExtractSolver.cs    SQL 数据提取
      JsonExtractSolver.cs   JSON 字段抽取（优先 id，保留大整数）
      JsonViewSolver.cs      JSON 预览
      JsonDiffSolver.cs      VSCode JSON Diff
      MybatisExtractSolver.cs MyBatis 注解提取 + 日志解析
      JsUserScriptSolver.cs  Jint 用户脚本加载器
    Services/
      SettingsService.cs     JSON 配置（accelerator / skipList / autoLaunch）
      HotkeyService.cs       SharpHook 全局快捷键（解析 "Ctrl+Alt+D" 风格）
      SingleInstance.cs      命名 Mutex + 命名管道
      AutoLaunchService.cs   三平台开机自启
      JsonCrackServer.cs     Kestrel 托管 jsoncrack 静态资源（端口 9987-10087）
      JsonHeroService.cs     Kestrel 内存 API + JSON Hero SPA 托管（端口 13001-13101）
      JsonPreviewService.cs  预览入口 + 系统浏览器打开
      VsCodeDiffService.cs   code --diff 临时文件对比
    Assets/
      fun-icon/*.svg         功能图标（复用 Electron 版资源）
      app-icon/tray-icon.png 托盘图标
```

## 5. 核心流程

### 5.1 唤醒 → 打分 → 执行

```
SharpHook KeyPressed (匹配 accelerator)
  → Dispatcher.UIThread → MainWindow.Show/Activate
  → Window.Activated → Clipboard.GetTextAsync()
  → MainWindowViewModel.AutoSelect(text)
      jsonFlag = ([...] 或 {...})
      对每个可见 solver 调 Check(str, strArr, jsonFlag)，取最高分选中
  → 用户按 Enter / 点击按钮
  → Execute(): selected.Transfer(...)
      结果写回 BodyText → Clipboard.SetTextAsync(result)
      solver.NextStep 存在时自动切换选中
  → Esc / 失焦 → HideAndReset()（清空文本并隐藏）
```

### 5.2 用户脚本（Jint）

- 目录：`%APPDATA%/str-toolkit-avalonia/user-scripts`（Linux/macOS 为对应 ApplicationData 路径）
- 启动时扫描 `*.js`，用 Jint `Engine.Modules.Import` 按 ES Module 加载，读取导出的 `solver`
- `check` / `transfer` 通过 Jint Invoke 调用，与内置处理器同台打分
- 图标：与脚本同名的 `.svg` / `.png`
- 限制见 DIFFERENCES.md（无 Node API / require / CryptoJS 注入）

### 5.3 JSON 预览

- jsoncrack：Kestrel 静态站点 + `/api/json-str?uuid=`（10 分钟过期缓存）。发布前读取
  `AykutSarac/jsoncrack.com` 最新 GitHub Release，临时构建源码，只复制 `apps/www/out` 到
  应用目录 `json-crack/`
- jsonhero：使用 `changdy/json-hero-frontend` 最新 GitHub Release 中的预构建静态文件；
  Kestrel 实现创建、读取、改名、
  删除、URL 获取及预览 API。文档只保存在当前进程内存中，默认 24 小时过期、最多 500 条，
  应用退出后全部清空
- 「JSON 预览」执行时：优先打开 jsonhero，同时打开 jsoncrack（与 Electron 版一致）

## 6. 构建与发布

```bash
# 开发运行
dotnet run --project src/StrToolkit

# 各平台自包含单文件发布
dotnet publish src/StrToolkit -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r osx-x64   --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

`dotnet publish` 会调用 `scripts/prepare-web-assets.mjs`：直接下载 JSON Hero latest Release 静态
产物，并在系统临时目录构建 JSONCrack latest Release 源码。发布目录只复制 JSON Hero 静态
目录和 JSONCrack 的 `apps/www/out`，不包含源码、`node_modules` 或包管理文件。
后续可在 GitHub Actions 中按 Electron 版的多平台矩阵配置自动发布。

## 7. 测试情况

- `dotnet build` 通过（0 warning / 0 error）
- 8 个内置处理器核心逻辑已用样例输入逐一验证（id-join 三态切换、排序去重数值/字符串、
  命名三风格轮转、SQL UPDATE/INSERT 提取、JSON 大整数抽取、MyBatis 注解与日志还原）
- GUI / 托盘 / 全局快捷键需在真实桌面环境人工验证
