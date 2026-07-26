# 字符串工具 V3 — Avalonia 版

![platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![runtime](https://img.shields.io/badge/runtime-.NET%208-512BD4)

## 项目简介

`字符串工具 V3 — Avalonia 版` 是一款面向日常开发场景的跨平台桌面小工具，主要用于对
**剪贴板中的文本进行快速识别、转换和预览**。

应用启动后常驻系统托盘。复制一段文本，再按下快捷键
`Ctrl+Alt+D`（macOS 为 `Command+Option+D`），工具会读取剪贴板内容，并自动选择最匹配的
处理功能。按 `Enter` 或点击按钮即可执行，结果会自动写回编辑区并复制到剪贴板。

它适合处理这些高频任务：

- **SQL 清洗**：从 DataGrip 等工具导出的 `UPDATE / INSERT` 语句中批量提取数据
- **MyBatis 还原**：从注解或运行日志中还原可直接执行的 SQL
- **JSON 辅助处理**：抽取字段、查看结构、对比两个对象的差异
- **文本整理**：排序去重、ID 拼接、变量命名风格转换
- **私有工具扩展**：通过 JavaScript 用户脚本添加团队或个人专用功能

本项目由 Electron 版
[`string_utils_v2`](https://github.com/changdy/string_utils_v2) 重写而来，使用 Avalonia UI 和
.NET 8，在保留主要使用方式和功能的同时，减少对 Electron / Node.js 运行时的依赖。

## 使用方式

1. **复制文本**：将待处理的内容复制到剪贴板
2. **唤醒工具**：Windows / Linux 按 `Ctrl+Alt+D`，macOS 按 `Command+Option+D`
3. **确认功能**：程序会自动选中最匹配的功能，也可以点击右侧图标手动切换
4. **执行处理**：按 `Enter` 或点击界面按钮
5. **获取结果**：结果会显示在编辑区，并自动复制回剪贴板

补充说明：

- 按 `Esc` 或点击窗口外部，窗口会自动隐藏
- 点击托盘图标可以显示或隐藏窗口
- 托盘菜单可以控制功能显隐、修改快捷键、设置开机启动和打开用户脚本目录
- 部分处理器会自动衔接下一步，例如“JSON 字段抽取 → 排序去重 → ID 拼接”

## 功能一览

| 功能 | 说明 |
|------|------|
| SQL 数据提取 | 从批量 `UPDATE / INSERT` 语句中提取目标值，适合整理 DataGrip 等工具生成的 SQL |
| 排序并去重 | 对多行文本进行排序和去重；常见数字会按数值大小排列 |
| 命名规则转换 | 在 `camelCase`、`snake_case`、`PascalCase` 等常用命名风格之间快速转换 |
| MyBatis 解析 | 从 `@Select / @Update / @Insert / @Delete` 注解或 MyBatis 日志中还原 SQL |
| JSON 预览 | 使用本机启动的 JSON Hero 和 JSONCrack 查看 JSON 的树形或节点结构 |
| JSON 字段抽取 | 从对象数组中批量抽取字段；优先提取 `id`，否则提取每项的第一个字段 |
| ID 拼接 | 在换行、逗号、`"a","b"` 等常用格式之间快速切换 |
| JSON Diff | 将包含两个对象的 JSON 数组交给 VS Code 进行可视化差异对比 |
| 用户脚本 | 加载个人或团队编写的 JavaScript 处理器，并像内置功能一样参与自动匹配 |

## JSON 预览与对比

JSON 预览会在本机启动两个服务：

- **JSON Hero**：以树形界面浏览 JSON
- **JSONCrack**：以节点图查看 JSON 结构

普通的本地 JSON 预览不依赖公网，预览内容保存在应用进程内存中，退出应用后会被清除。

`JSON Diff` 的输入必须是一个包含两个对象的 JSON 数组，例如：

```json
[
  { "id": 1, "name": "before" },
  { "id": 1, "name": "after" }
]
```

执行后会调用 VS Code 的差异对比界面。使用此功能前，建议先安装 VS Code，并确保
`code` 命令可用。

## 下载安装

前往 [Releases](../../releases) 页面下载对应平台的软件包：

- **Windows**：`StrToolkit-Setup-win-x86.exe`
- **macOS Apple Silicon**：`StrToolkit-osx-arm64.tar.gz`
- **macOS Intel**：`StrToolkit-osx-x64.tar.gz`
- **Linux x64**：`StrToolkit-linux-x64.tar.gz`

macOS 首次使用全局快捷键时，需要在“系统设置 → 隐私与安全性 → 辅助功能”中授权。
Linux Wayland 环境通常无法使用全局快捷键，此时仍可通过托盘图标唤醒工具。

## 用户脚本

应用支持通过 JavaScript 添加自定义处理功能。可以从托盘菜单点击“打开用户脚本目录”，
将脚本放入该目录后重启应用。

推荐每个脚本使用独立目录：

```text
user-scripts/
  my-tool/
    index.js
    icon.svg
```

Avalonia 版兼容原有的 `solver` 处理器接口，但脚本运行在内嵌 JavaScript 引擎中，并不是
Node.js 环境。纯字符串、正则和 JSON 处理脚本通常可以直接迁移；依赖 Node API 的脚本需要
调整。用户脚本能够读取当前进程可见的环境变量，因此只应安装可信来源的脚本。

脚本格式、内置库、迁移限制和示例请查看
[技术与开发文档](TECHNICAL.md#用户脚本开发)。

## 开发

本地开发需要 .NET 8 SDK、Node.js 24+、Corepack 和 `tar`：

```bash
dotnet run --project src/StrToolkit
```

首次构建会准备 JSON Hero 和 JSONCrack 的本地静态资源，可能需要数分钟；后续构建会复用缓存。
完整的资源准备、项目结构、打包与自动发布说明请查看 [技术与开发文档](TECHNICAL.md)。

## 相关文档

- [技术与开发文档](TECHNICAL.md)：本地开发、用户脚本 API、资源准备、打包与发布
- [迁移设计方案](DESIGN.md)：Avalonia 版的架构设计与技术选型
- [与 Electron 版的差异](DIFFERENCES.md)：平台行为、兼容性和已知限制
