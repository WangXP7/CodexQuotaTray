# Codex Quota Tray

一个原生 Windows 托盘小工具，实时显示 Codex 剩余额度。高对比圆角方形托盘图标直接显示剩余百分比，数字使用 Windows 原生的 Microsoft YaHei UI 粗体，两位数固定使用清晰的 13 px 大字号；左键打开详情卡片，右键可刷新、固定悬浮窗、复制摘要和设置开机启动。

## 直接运行

双击：

```text
dist\CodexQuotaTray.exe
```

手动运行时会展开详情卡片；随 Windows 自动启动时只常驻托盘。

Windows 可能会先把新图标放进托盘折叠区。可以将图标拖到任务栏通知区域，或在“任务栏设置 → 选择哪些图标显示在任务栏上”中将它打开。

图标颜色：

- 绿色：剩余 50% 或更多
- 橙色：剩余 20%–49%
- 红色：剩余不足 20%
- 灰色 `?`：等待数据或暂不可用

同一额度桶有多个时间窗口时，图标显示其中较低的剩余百分比；独立计量的其它额度桶只在详情卡片和复制摘要中单列，不会混入主值。

## 数据来源与隐私

程序优先短时连接本机 Codex 自带的 App Server，通过官方 `account/rateLimits/read` 方法读取额度，每 60 秒主动校验一次；查询完成即释放子进程。Codex 会话文件有新事件时会立即触发本地刷新，因此平时不需要常驻额外的 Codex 服务进程。

实时接口暂不可用时，程序只解析 `%CODEX_HOME%\sessions` 中 Codex 已写入的最新 `token_count.rate_limits` 事件作为降级缓存。程序不会读取、复制或保存 `auth.json` 中的登录令牌，也不会直接调用私有网页接口。

“剩余百分比”按 `100 - usedPercent` 计算。Codex 的实际消耗会随任务复杂度、模型和运行位置变化；账户内的 Codex Usage 面板仍是最终口径。协议字段可参见 [Codex App Server 官方文档](https://developers.openai.com/codex/app-server)。

## 操作

- 左键托盘图标：显示/隐藏额度卡片
- 右键 → 固定悬浮窗：让卡片常驻桌面右下角
- 右键 → 立即刷新：重新读取实时额度和本地缓存
- 右键 → 随 Windows 启动：写入当前用户的启动项，无需管理员权限
- 右键 → 复制额度摘要：复制各窗口、套餐和更新时间
- 剩余量首次跨过 20%、10%、5% 时发出系统提醒

## 从源码构建

要求 Windows 10/11 和 .NET Framework 4.8。无需安装 Visual Studio、.NET SDK 或 NuGet：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

构建产物是单文件 `dist\CodexQuotaTray.exe`。如果移动 EXE，请重新勾选一次“随 Windows 启动”，让启动项记录新路径。

## 可选配置

如自动发现 Codex 失败，可在启动前设置：

```powershell
$env:CODEX_QUOTA_CODEX_PATH = 'C:\path\to\codex.exe'
```

程序也会尊重现有的 `CODEX_HOME`；未设置时使用 `%USERPROFILE%\.codex`。
