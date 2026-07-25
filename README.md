# 字符串工具 V2 — Avalonia 版

Electron 版 `str_toolkit` 的 Avalonia UI + .NET 8 跨平台重写。

- 设计方案：[DESIGN.md](DESIGN.md)
- 与 Electron 版的行为差异：[DIFFERENCES.md](DIFFERENCES.md)

## 快速开始

```bash
# 需要 .NET 8 SDK、Node.js 24+、Corepack 和 tar
dotnet run --project src/StrToolkit
```

Visual Studio/F5 或普通 Debug build 会在首次发现资源缺失时自动运行
`scripts/prepare-web-assets.mjs`。第一次构建需要下载并编译 JSONCrack，耗时通常为数分钟；之后
直接复用 `.web-assets/`，不会在每次调试时访问 GitHub。构建结束后，两套静态资源会复制到
`bin/Debug/net8.0/`，应用运行时只访问这个本地输出目录。

默认快捷键为 `CommandOrControl+Alt+D`（Windows/Linux 对应 `Ctrl+Alt+D`，macOS 对应
`Command+Option+D`）唤醒浮窗；托盘菜单可修改快捷键、控制功能显隐、设置开机自启。

## 用户脚本

用户脚本目录为：

- Windows：`%APPDATA%\str-toolkit-avalonia\user-scripts`
- macOS/Linux：系统 ApplicationData 下的 `str-toolkit-avalonia/user-scripts`

可通过托盘菜单「打开用户脚本目录」直接打开。新增或修改脚本后需要重启应用。
用户脚本完全由用户自行放入该目录；仓库中的示例不会随应用构建、发布或自动安装。

### 推荐的脚本包格式

每个脚本使用独立目录，依赖、图标和入口互不干扰：

```text
user-scripts/
  decrypt/
    index.js
    icon.svg
    licenses/
```

约定如下：

- 一级目录名是脚本包 ID。
- `index.js` 是固定 ES Module 入口。
- `icon.svg` 是默认图标；也兼容 `icon.png`，图标可以省略。
- 其他 `.js` / `.mjs` 文件和子目录均可作为包内依赖。

入口必须导出 `solver`：

```js
export const solver = {
    name: "example",
    describe: "示例脚本",
    nextStep: "",

    check(logs, arr, jsonFlag) {
        return 0;
    },

    transfer(logs, arr, jsonFlag) {
        return logs;
    }
};
```

为兼容已有脚本，根目录下的单个 `*.js` 仍会加载；其同名 `.svg` / `.png` 仍作为图标。
旧格式和目录型脚本包使用相同的客户端 API。

### 客户端 API

客户端提供通用环境变量读取，并内置部分通用 JavaScript 库：

```js
const key = strToolkit.env.get("jsutils_key");
const path = strToolkit.env.get("PATH");
```

`env.get(name)` 可以读取当前 StrToolkit 进程可见的任意环境变量；不存在时返回空字符串。
因此用户脚本应视为受信任代码，不应直接安装来源不明的脚本包。

当前内置 Lodash 4.18.1、Day.js 1.11.21 和 CryptoJS 4.2.0，并提供 UTF-8 感知的
`base64Encode` / `base64Decode`。用户脚本无需安装 npm 依赖：

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

这里的 `require` 只用于获取应用内置库，不是 Node.js 的模块加载器。请求其他模块会直接报错。
三个库会在每个用户脚本的独立 Jint 引擎创建时无条件加载。

#### 已知限制

- Jint 没有浏览器事件循环和 `setTimeout` / `clearTimeout`。Lodash 的集合、对象、数组和字符串
  工具可以正常使用，但 `_.debounce`、`_.throttle`、`_.delay`、`_.defer` 等定时器相关方法
  不受支持。
- 当前只内置 Day.js 核心，不包含额外插件和语言包。`require("dayjs/plugin/...")` 与
  `require("dayjs/locale/...")` 不受支持；时区和本地时间结果还会受到操作系统及 Jint
  日期实现影响。
- `base64Encode` / `base64Decode` 按 UTF-8 编解码，直接支持中文等 Unicode 文本；
  不提供浏览器风格的 `atob` / `btoa`。`base64Decode` 容忍输入中的空白字符和缺失的
  填充，非法 Base64 输入抛出 `TypeError`；解码结果不是有效 UTF-8 文本时报错。
- 用户脚本的 `check` / `transfer` 是同步调用。不要依赖定时回调、网络请求或未等待的 Promise。
- 所有库都会为每个用户脚本引擎各加载一份。少量脚本影响很小，但大量脚本会增加启动时间和内存。
- `_.template` 等能够执行模板表达式的 API 只应处理可信输入；用户脚本本身也应视为受信任代码。

### 管理第三方依赖

Jint 支持包目录内的相对 ES Module 导入：

```js
import helper from "./lib/helper.mjs";
```

Jint 不是 Node.js，不提供通用的 `require`、`process`、`Buffer`、Node 内置模块或
`node_modules` 包名解析。白名单内置库可以通过上述 `require` 使用；其他纯 ESM 依赖可以
随包放入 `lib/`，npm/CommonJS 依赖建议在开发阶段打包为单个 ESM：

```bash
npm install
npx esbuild src/index.js \
  --bundle \
  --format=esm \
  --platform=browser \
  --target=es2020 \
  --outfile=dist/index.js
```

仓库中的 `user-scripts-src/decrypt` 是完整示例：它直接使用应用内置的 `crypto-js`，
无需声明或打包该运行依赖，也不需要 `package.json`、`npm install` 或 esbuild。
这个目录只作为源码和实现参考，不参与应用打包。实际使用时，由用户把 `index.js`、
`rsa.js` 等运行文件复制到自己的用户脚本目录；目标机器不需要 Node.js、npm 或联网。

decrypt 示例的测试也没有第三方依赖，可以直接运行：

```bash
node --test user-scripts-src/decrypt/test/decrypt.test.mjs
```

## JSON 预览资源

JSON Hero 已改为前后端分离架构：

- 前端：`changdy/json-hero-frontend` 最新 GitHub Release 中的预构建静态文件
- 后端：本项目内嵌 Kestrel API，文档仅保存在进程内存中
- 运行时不再依赖 Node.js，也不会把预览数据写入磁盘

JSONCrack 的 GitHub Release 只提供源码，因此资源准备脚本会下载最新 Release 源码，在系统临时
目录执行 `pnpm install` 和 `pnpm build:www`，然后只保留 `apps/www/out` 静态导出目录。

手动准备或更新两套资源：

```bash
node scripts/prepare-web-assets.mjs

# 即使 latest 标签未变化也重新下载和构建
node scripts/prepare-web-assets.mjs --force
```

普通 Debug build 只在资源缺失时准备；要主动检查 latest 是否变化，可手动执行第一条命令。
正式 `dotnet publish` 每次都会检查 latest，但版本未变化时会立即复用缓存。

最终静态文件缓存在被 Git 忽略的 `.web-assets/`：

```text
.web-assets/
  jsonhero-frontend/   # JSON Hero Release 预构建产物
  json-crack/          # JSONCrack apps/www/out
  versions.json        # 本次解析到的 latest Release 标签
```

脚本不会将 JSONCrack 源码、`node_modules`、npm/pnpm 缓存或包管理锁文件复制到该目录；临时
构建目录无论成功失败都会清理。

只启动 JSON Hero C# API（供 Vite 开发服务器使用）：

```bash
dotnet run --project src/StrToolkit -- --jsonhero-server
```

`dotnet publish` 会自动运行资源准备脚本，解析两边的 latest Release；版本未变化且缓存完整时
会直接复用 `.web-assets/`，否则更新 JSON Hero 并重新构建 JSONCrack。发布目录最终只包含
`jsonhero-frontend/`、`json-crack/` 静态文件和 `web-assets-versions.json` 版本记录，目标机器
运行时不需要 Node.js，也不需要联网。

## 发布

```bash
dotnet publish src/StrToolkit -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r osx-x64   --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

## GitHub Actions 自动打包

工作流 `.github/workflows/package.yml` 会生成以下 Actions Artifacts：

- Windows x86 安装程序：`StrToolkit-Setup-win-x86.exe`
- macOS Apple Silicon：`StrToolkit-osx-arm64.tar.gz`
- macOS Intel：`StrToolkit-osx-x64.tar.gz`
- Linux x64：`StrToolkit-linux-x64.tar.gz`

可在 GitHub Actions 页面手动运行 `Package` 生成 Artifacts，但手动运行不会发布 Release。
推送到 `main` 时，工作流会比较 `src/StrToolkit/StrToolkit.csproj` 中的 `<Version>` 与上一提交；
只有版本号发生变化时才会自动打包、创建对应的 `vX.Y.Z` 标签和 GitHub Release，并上传全部
四个平台安装包：

```xml
<Version>4.0.7</Version>
```

版本标签已存在但指向其他提交时，发布会直接失败，避免覆盖历史版本。
