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
