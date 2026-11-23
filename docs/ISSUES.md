# 问题记录

已发生、需要记住的问题。新问题按同样四段往下加：现象、原因、解决办法、如何避免。

## 2026-10-26　关机后机器又起来

悬浮球版本（当时进程名是 `PerchDesk.exe` / `SpiritNest.exe`，托盘常驻）。

### 现象

点关机后机器没有停住，又回到桌面，看起来像关机失败后又启动了一次。

在任务管理器里结束进程后再点关机，机器仍然起来。这时已经没有本程序的进程。

### 原因

有两层，不要并成一条。

1. **程序拦住了关机。** 悬浮球是托盘常驻。平时关掉窗口会取消关闭，只把球藏起来。系统关机、注销时如果也取消关闭，Windows 完不成关机，桌面又回来，看起来像重启。托盘图标没卸掉时，资源管理器还会等这个图标。
2. **进程已经没了之后，现象还在。** 当时为了让开机启动少等约 10 秒，写过当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize\StartupDelayInMSec = 0`。这项会取消这个用户所有开机启动项的延迟，后来已删除。进程、计划任务、`Run` 启动项都没有，旧工程里也没有残留的 exe。剩下对得上的是系统「快速启动」开着（`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power` 的 `HiberbootEnabled = 1`），这台电脑又是 S0 现代待机。开着快速启动时，关机会休眠内核会话，下次按电源像是立刻又启动。关掉它需要管理员权限，当时没有改成，所以这一层没有用「关掉后再关机」验证完。

### 解决办法

程序侧已经按这个处理，现在的灵动岛版本仍保留：

- 收到关机或注销（`SessionEnding`、`SystemEvents.SessionEnding`）时不取消，走 `RequestExitForShutdown`。
- 先卸掉托盘图标，再关窗口、再 `Shutdown`。`IsExiting` 置位后，不能再把关闭取消掉。
- 不再写入 `StartupDelayInMSec`。
- 开机启动用当前用户的登录计划任务，不设延迟，也不勾「错过计划后尽快运行」。安装版是 `SpiritPerch\Autostart`，开发版是 `SpiritPerchDev\Autostart`。关掉开关只删自己的任务。卸载安装版会删掉 `SpiritPerch\Autostart`、旧任务 `SpiritNest\Autostart` 和 Run 里的 `SpiritPerch` / `SpiritNest`，不删开发版任务。

系统侧若在程序已经退出后仍然「一关机就起来」：控制面板 → 电源选项 → 选择电源按钮的功能 → 更改当前不可用的设置 → 取消「启用快速启动」。或用管理员执行：

```text
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power" /v HiberbootEnabled /t REG_DWORD /d 0 /f
```

### 如何避免

- 关机、注销路径上禁止取消 `Closing`。要用 `SessionEnding` 和 `Environment.HasShutdownStarted` 区分「用户关窗口」和「系统正在关机」。
- 退出前必须先释放托盘图标（`TaskbarIcon.Dispose`）。
- 不要写 `Explorer\Serialize\StartupDelayInMSec`。要更快启动时，不要改所有启动项的延迟。
- 登录计划任务用来避开 `Run` 的约 10 秒延迟。安装版和开发版各用各的任务。关掉「开机启动」和卸载时必须删掉对应的那个任务，不要删另一个。不要加「错过计划后尽快运行」，关机未完成时它会把程序再拉起来。
- 排查顺序：先结束进程再关机。进程没了仍会起来，就查快速启动、等待中的 Windows 更新重启、S0 现代待机，不要继续改程序。
