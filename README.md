# 字符串工具 V2 — Avalonia 版

Electron 版 `str_toolkit` 的 Avalonia UI + .NET 8 跨平台重写。

- 设计方案：[DESIGN.md](DESIGN.md)
- 与 Electron 版的行为差异：[DIFFERENCES.md](DIFFERENCES.md)

## 快速开始

```bash
# 需要 .NET 8 SDK
dotnet run --project src/StrToolkit
```

默认快捷键为 `CommandOrControl+Alt+D`（Windows/Linux 对应 `Ctrl+Alt+D`，macOS 对应
`Command+Option+D`）唤醒浮窗；托盘菜单可修改快捷键、控制功能显隐、设置开机自启。

## JSON 预览资源

JSON Hero 已改为前后端分离架构：

- 前端：根目录同级项目 `../json-hero-frontend`（React + Vite）
- 后端：本项目内嵌 Kestrel API，文档仅保存在进程内存中
- 运行时不再依赖 Node.js，也不会把预览数据写入磁盘

开发和发布前先构建前端：

```bash
cd ../json-hero-frontend
npm ci
npm run build
```

只启动 JSON Hero C# API（供 Vite 开发服务器使用）：

```bash
dotnet run --project src/StrToolkit -- --jsonhero-server
```

`dotnet build/publish` 会把已存在的 `../json-hero-frontend/dist/` 自动复制为应用输出目录下的
`jsonhero-frontend/`。JsonCrack 仍需将 Electron 项目的 `json-crack/` 构建产物复制到应用目录。

## 发布

```bash
dotnet publish src/StrToolkit -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r osx-x64   --self-contained -p:PublishSingleFile=true
dotnet publish src/StrToolkit -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```
