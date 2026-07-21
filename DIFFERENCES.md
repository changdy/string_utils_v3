# 与 Electron 版的行为差异记录

按约定，Avalonia 实现与 Electron 版效果不一致的地方统一记录在此。

## 1. 窗口与交互

| 项 | Electron 版 | Avalonia 版 | 说明 |
|----|-------------|-------------|------|
| 唤醒位置 | 窗口出现在鼠标光标附近 (`cursor.x-180, cursor.y-100`) | Windows、macOS、Linux/X11 与 Electron 一致；Linux/Wayland 居中偏上 | Avalonia 没有统一的全局光标 API，当前分别调用 Win32、CoreGraphics 和 Xlib。Wayland 出于安全限制不提供通用的全局坐标读取能力 |
| 窗口透明/圆角 | `opacity: 0.9` + CSS 圆角 + 无阴影 | `Opacity=0.9` + Border 圆角 | 视觉近似；Linux 某些合成器下透明度表现可能不同 |
| 选中图标动画 | CSS 无限渐变 + 图标 hover 360° 翻转 | Hover 整体按 1.2 倍参与布局放大（相邻按钮保持间距并随之移动）、点击压缩回弹、单次高光、选中轮廓滑动 | 改为短促且有状态含义的动效；动画结束后保持静止 |
| Enter 按钮气泡动画 | bubbly-button CSS radial-gradient 粒子动画 | Avalonia 原生绘制的上下气泡散开 + 按钮缩放回弹 | 粒子轨迹不逐像素复刻，时长和整体观感接近 |

Enter 按钮粒子参数调整记录：

- 初始实现：粒子半径 `2.8-5.2` 逻辑像素；水平移动 `0-13`、垂直移动 `24-48`、弧线偏移 `1.2-2.0` 逻辑像素。
- 当前参数：原始粒子大小按比例映射到半径 `1-3` 逻辑像素；水平、垂直和弧线移动均为初始实现的 `1/3`，实际水平移动 `0-4.33`、垂直移动 `8-16`、弧线偏移 `0.4-0.67` 逻辑像素。
- 曾试用尺寸 `1/12`、垂直移动 `1/9` 的参数，但粒子进入亚像素范围后几乎不可见，因此已恢复到上述当前参数。
- 粒子的初始分布位置、数量（上 9 下 7）和 `560ms` 动画时长保持不变。
| 文本区 | `contenteditable div` | `TextBox` | 行为基本一致；富文本粘贴会被转纯文本（更符合工具语义） |
| 字体 | 内嵌 JetBrainsMono woff2 | 优先使用系统已装 JetBrains Mono，回退 monospace | 如需完全一致可后续内嵌 ttf |

## 2. 全局快捷键

- 实现由 Electron `globalShortcut`（系统注册式）换成 SharpHook（libuiohook 全局监听式）。监听启动失败时不再误报注册成功，托盘提示会显示降级状态。
- Windows 和 Linux/X11 可直接使用；默认 `CommandOrControl+Alt+D` 在 Windows/Linux 映射为 `Ctrl+Alt+D`。
- **Linux Wayland 不支持静默的全局按键监听**，应用会保留托盘入口并显示明确提示。
- macOS 默认映射为 `Command+Option+D`；首次运行需在「系统设置 → 隐私与安全性 → 辅助功能」授权。
- Windows/macOS 会阻止已匹配的主键继续传给前台应用；Linux 受底层限制不阻止按键传播。
- 按住快捷键时只触发一次，松开主键后才能再次触发，避免系统按键重复造成连续唤醒。
- 快捷键匹配要求修饰键精确一致（多按修饰键不触发），与 Electron 行为一致。

## 3. 用户脚本（最主要差异）

Jint 是纯 .NET 的 JS 解释器，**没有 Node.js 运行时**，因此 Electron 版注入的以下能力不可用：

| 注入项 | Electron 版 | Avalonia 版 |
|--------|-------------|-------------|
| `require` | 指向应用 node_modules，可加载任意已装依赖 | ❌ 不支持 |
| `CryptoJS` | crypto-js | ❌ 暂未桥接 |
| `nodeCrypto` | Node `crypto` | ❌ 不支持 |
| `forge` | node-forge | ❌ 暂未桥接 |
| ES Module 相对导入 | 支持 | ✅ 支持（限脚本目录内的纯 JS 模块） |

即：**只依赖纯 JS 逻辑（字符串/正则/JSON 处理）的用户脚本可以原样运行**；
依赖 Node API 或加密库的脚本需要改造。后续可用 .NET `System.Security.Cryptography`
桥接一个 `CryptoJS` 兼容层按需补齐。

其他差异：

- `solver.style`（向页面注入 CSS）无对应物，**忽略该字段**。图标微调类样式不再生效。
- 用户脚本目录改为 `str-toolkit-avalonia/user-scripts`（新应用独立配置目录，不与 Electron 版互抢）。
- 与 Electron 版相同：新增/修改脚本后需重启应用。

## 4. JSON 预览

- jsoncrack：由 Kestrel 原生托管，行为与 Electron 版一致（需将 `json-crack` 构建产物复制到应用目录）。
- jsonhero：前端已拆分到同级 `json-hero-frontend` 项目，服务端 API 由 Avalonia 进程内的
  Kestrel 实现，不再启动 Node.js。构建后的 SPA 与 API 使用同一个 loopback 地址。
- 文档只保存在内存中（最多 500 条、默认 24 小时过期），应用退出或重启后清空；这符合本地
  预览用途，但与可跨进程保存/分享的线上 JSON Hero 不同。
- URL 文档和 URL 内容预览仍可能访问用户指定的网络地址；普通本地 JSON 预览完全离线。

## 5. JSON Diff

- 行为一致（临时文件 + `code --diff`，含各平台 fallback 路径）。
- Electron 版用 json-bigint 解析后重新格式化，大整数完整保留；
  Avalonia 版用 `System.Text.Json` 原样序列化，大整数同样保留（`JsonElement` 保留原始文本）。

## 6. 排序 & 去重（sort-distinct）

- Electron 版用 bignumber.js（任意精度）；Avalonia 版用 `decimal`（28-29 位有效数字）。
  超过 decimal 精度的超长数字串会退化为字符串排序。日常 ID/数值场景无差异。

## 7. 配置与日志

- 配置由 electron-store 换成 JSON 文件：`<ApplicationData>/str-toolkit-avalonia/settings.json`，
  键名有调整（accelerator / skipList / autoLaunch），不读取旧 Electron 配置。
- electron-log 的文件日志暂未对应实现，当前输出到 stdout/stderr。可后续接入 Serilog。

## 8. 开机自启

- Windows：注册表 `HKCU\...\Run`（Electron 版用 `setLoginItemSettings`，效果等价）
- macOS：`~/Library/LaunchAgents` plist（Electron 版为 Login Items，表现略有差异：不出现在「登录项」列表）
- Linux：`~/.config/autostart/*.desktop`（等价）
