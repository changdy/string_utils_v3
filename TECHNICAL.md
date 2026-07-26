# 字符串工具 V2 — 技术与开发文档

本文档集中记录 Avalonia 版的开发环境、用户脚本接口、Web 资源准备、构建和发布流程。
面向普通使用者的功能介绍请查看 [README.md](README.md)；架构背景和平台差异分别见
[DESIGN.md](DESIGN.md) 与 [DIFFERENCES.md](DIFFERENCES.md)。

## 本地运行

### 环境要求

- .NET 8 SDK
- Node.js 24+
- Corepack
- `tar`

启动桌面应用：

```bash
dotnet run --project src/StrToolkit
```

Visual Studio / F5 或普通 Debug build 会在首次发现资源缺失时自动运行
`scripts/prepare-web-assets.mjs`。第一次构建需要下载并编译 JSONCrack，通常耗时数分钟；之后
会直接复用 `.web-assets/`，不会在每次调试时访问 GitHub。

构建结束后，JSON Hero 和 JSONCrack 两套静态资源会复制到 `bin/Debug/net8.0/`。应用运行时
只访问本地输出目录。

## 项目结构

```text
src/StrToolkit/
  Program.cs                 应用入口与单实例处理
  App.axaml(.cs)             服务装配、托盘和全局快捷键
  Views/                     Avalonia 窗口及自定义控件
  ViewModels/                自动匹配、执行流程及界面状态
  Solvers/                   内置处理器和 JavaScript 用户脚本加载器
  Services/                  配置、快捷键、预览服务、开机启动等
  Assets/
    fun-icon/                内置功能图标
    app-icon/                应用和托盘图标
    JsRuntime/               用户脚本可使用的内置 JavaScript 库
scripts/
  prepare-web-assets.mjs     准备 JSON Hero / JSONCrack 静态资源
installer/windows/
  StrToolkit.iss             Windows Inno Setup 安装包配置
.github/workflows/
  package.yml                多平台构建与 Release 发布流程
```

更完整的架构与核心流程说明见 [DESIGN.md](DESIGN.md)。

## 用户脚本开发

### 脚本目录

用户脚本目录为：

- Windows：`%APPDATA%\str-toolkit-avalonia\user-scripts`
- macOS / Linux：系统 ApplicationData 下的 `str-toolkit-avalonia/user-scripts`

也可以通过托盘菜单“打开用户脚本目录”直接打开。脚本只在应用启动时扫描，因此新增或修改
脚本后需要重启应用。

仓库中的 `user-scripts-src` 用作源码和实现参考，不参与默认构建或发布。实际使用时，由用户
将需要的运行文件复制到自己的用户脚本目录。

### 推荐的脚本包格式

每个脚本使用独立目录，使依赖、图标和入口互不干扰：

```text
user-scripts/
  my-tool/
    index.js
    icon.svg
    lib/
      helper.mjs
```

约定如下：

- 一级目录名是脚本包 ID
- `index.js` 是固定 ES Module 入口
- `icon.svg` 是默认图标，也兼容 `icon.png`；图标可以省略
- 其他 `.js` / `.mjs` 文件和子目录可作为包内依赖

为兼容旧脚本，用户脚本根目录下的单个 `*.js` 仍会加载，其同名 `.svg` / `.png` 仍作为图标。
两种格式使用相同的客户端 API。

### `solver` 接口

入口必须导出一个 `solver` 对象：

```js
export const solver = {
    name: "my-tool",
    describe: "我的工具",
    nextStep: "",

    check(logs, arr, jsonFlag) {
        return arr.length > 0 && !jsonFlag ? 100 : 0;
    },

    transfer(logs, arr, jsonFlag) {
        return arr.join(",");
    }
};
```

字段与方法说明：

| 成员 | 说明 |
|------|------|
| `name` | 脚本的唯一标识，也用于功能显隐和图标匹配 |
| `describe` | 显示在托盘菜单和界面提示中的功能名称 |
| `nextStep` | 可选；处理完成后自动切换到指定处理器 |
| `check(logs, arr, jsonFlag)` | 返回匹配分数，分数越高越容易被自动选中 |
| `transfer(logs, arr, jsonFlag)` | 执行处理并返回结果文本 |

其中，`logs` 是原始文本，`arr` 是按行拆分后的数组，`jsonFlag` 表示输入是否被识别为 JSON。
`check` 和 `transfer` 都是同步调用，不应依赖异步回调、网络请求或未等待的 Promise。

Electron 版的 `solver.style` 没有对应能力，Avalonia 版会忽略该字段。

### 客户端 API 与内置库

脚本可以通过 `strToolkit.env.get(name)` 读取当前 StrToolkit 进程可见的环境变量：

```js
const key = strToolkit.env.get("jsutils_key");
const path = strToolkit.env.get("PATH");
```

环境变量不存在时返回空字符串。由于脚本能够读取进程环境，应将用户脚本视为受信任代码，
不要安装来源不明的脚本包。

应用内置 Lodash 4.18.1、Day.js 1.11.21 和 CryptoJS 4.2.0，并提供 UTF-8 感知的
`base64Encode` / `base64Decode`：

```js
const _ = require("lodash");
const dayjs = require("dayjs");
const CryptoJS = require("crypto-js");

const unique = _.uniq([1, 1, 2]);
const date = dayjs("2026-07-25").format("YYYY/MM/DD");
const digest = CryptoJS.SHA256("hello").toString();
const encoded = base64Encode("你好，世界");
const decoded = base64Decode(encoded);
```

这里的 `require` 只用于获取应用白名单中的内置库，并不是 Node.js 模块加载器。请求其他模块
会直接报错。三个库会在每个用户脚本独立的 Jint 引擎创建时加载。

### 已知限制

- Jint 不是 Node.js，不提供通用 `require`、`process`、`Buffer`、Node 内置模块或
  `node_modules` 包名解析
- Jint 没有浏览器事件循环，也没有 `setTimeout` / `clearTimeout`
- Lodash 的集合、对象、数组和字符串工具可以正常使用，但 `_.debounce`、`_.throttle`、
  `_.delay`、`_.defer` 等定时器相关方法不受支持
- 当前只内置 Day.js 核心，不包含插件和语言包；`require("dayjs/plugin/...")` 和
  `require("dayjs/locale/...")` 不受支持
- `base64Encode` / `base64Decode` 使用 UTF-8，不提供浏览器风格的 `atob` / `btoa`
- `base64Decode` 容忍空白字符和缺失的填充；非法 Base64 或无效 UTF-8 结果会报错
- 所有内置库都会为每个脚本引擎各加载一份，大量脚本会增加启动时间和内存占用
- `_.template` 等能执行模板表达式的 API 只应处理可信输入

完整迁移差异见 [DIFFERENCES.md](DIFFERENCES.md#3-用户脚本最主要差异)。

### 管理第三方依赖

Jint 支持脚本包目录内的相对 ES Module 导入：

```js
import helper from "./lib/helper.mjs";
```

其他纯 ESM 依赖可以随脚本放入 `lib/`。npm / CommonJS 依赖建议在开发阶段打包为单个 ESM：

```bash
npm install
npx esbuild src/index.js \
  --bundle \
  --format=esm \
  --platform=browser \
  --target=es2020 \
  --outfile=dist/index.js
```

仓库中的 `user-scripts-src/decrypt` 是完整示例。它直接使用应用内置的 `crypto-js`，无需声明
该运行依赖，也不需要 `package.json`、`npm install` 或 esbuild。目标机器不需要 Node.js、
npm 或联网。

示例测试没有第三方依赖，可以直接运行：

```bash
node --test user-scripts-src/decrypt/test/decrypt.test.mjs
```

## JSON 预览资源

JSON Hero 使用前后端分离架构：

- 前端：`changdy/json-hero-frontend` 最新 GitHub Release 中的预构建静态文件
- 后端：应用内嵌的 Kestrel API
- 存储：文档只保存在进程内存中，不写入磁盘

JSONCrack 的 GitHub Release 只提供源码，因此资源准备脚本会下载最新 Release 源码，在系统
临时目录执行 `pnpm install` 和 `pnpm build:www`，然后只保留 `apps/www/out` 静态导出目录。

手动准备或更新两套资源：

```bash
node scripts/prepare-web-assets.mjs

# 即使 latest 标签未变化也重新下载和构建
node scripts/prepare-web-assets.mjs --force
```

普通 Debug build 只在资源缺失时准备。若要主动检查 latest 是否变化，可手动执行第一条命令。
正式 `dotnet publish` 每次都会检查 latest，但版本未变化时会复用缓存。

最终静态文件缓存在被 Git 忽略的 `.web-assets/`：

```text
.web-assets/
  jsonhero-frontend/   JSON Hero Release 预构建产物
  json-crack/          JSONCrack apps/www/out
  versions.json        当前解析到的 latest Release 标签
```

脚本不会将 JSONCrack 源码、`node_modules`、npm / pnpm 缓存或锁文件复制到该目录；临时构建
目录无论成功失败都会清理。

只启动 JSON Hero C# API，供 Vite 开发服务器使用：

```bash
dotnet run --project src/StrToolkit -- --jsonhero-server
```

发布目录只包含 `jsonhero-frontend/`、`json-crack/` 静态文件和
`web-assets-versions.json`。目标机器运行时不需要 Node.js，也不需要联网。

## 本地发布

```bash
dotnet publish src/StrToolkit -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r osx-x64   --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

`dotnet publish` 会自动调用 `scripts/prepare-web-assets.mjs`。发布目录只复制 JSON Hero 和
JSONCrack 的静态产物，不包含源码、`node_modules` 或包管理文件。

## GitHub Actions 自动打包

工作流 `.github/workflows/package.yml` 会生成以下 Actions Artifacts：

- Windows x86 安装程序：`StrToolkit-Setup-win-x86.exe`
- macOS Apple Silicon：`StrToolkit-osx-arm64.tar.gz`
- macOS Intel：`StrToolkit-osx-x64.tar.gz`
- Linux x64：`StrToolkit-linux-x64.tar.gz`

可在 GitHub Actions 页面手动运行 `Package` 生成 Artifacts，但手动运行不会发布 Release。

当 `src/StrToolkit/StrToolkit.csproj` 的变更推送到 `master` 时，工作流会比较其中的
`<Version>` 与上一提交。版本号发生变化，或当前版本对应的标签尚不存在时，会自动打包、
创建对应的 `vX.Y.Z` 标签和 GitHub Release，并上传全部四个平台的软件包：

```xml
<Version>4.0.7</Version>
```

如果版本标签已存在但指向其他提交，发布会直接失败，以免覆盖历史版本。
