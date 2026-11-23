# 灵栖 Spirit Perch

版本 **0.1.0**。屏幕顶部的灵动岛：收纳桌面快捷方式与网址，悬停展开，离开后收起。

工程标识：`SpiritNest`（无空格）。产品名：灵栖 / Spirit Perch。

| 文档 | 内容 |
|------|------|
| [docs/DESIGN.md](docs/DESIGN.md) | 产品决策、数据、非目标 |
| [docs/INTERACTION.md](docs/INTERACTION.md) | 状态、手势、各表面行为 |
| [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md) | 颜色、尺寸、动效 |

## 当前能力

- 单实例，托盘常驻。已有实例时再次启动不做任何事
- 主屏工作区顶部居中的灵动岛：温度、双眼时分、天气；可切成极简细条
- 悬停展开面板：按页面翻页的图标、搜索、天气。位置不可拖
- 设置里管理页面与图标：拖入收纳、添加网址、排序、删除时询问是否还原到原路径
- 失效项变淡且不能启动
- 全屏时自动隐藏（可关）
- 安装包：`installer/SpiritNest.iss`，版本与程序集同为 0.1.0

## 运行

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet run --project src\SpiritNest.App\SpiritNest.App.csproj
```

日常编译输出在 `bin\`。发布到桌面快捷方式所用的 `run\` 时，用项目根目录的 `_pub.ps1`（先经命名管道优雅退出，再发布）。不要对正在运行的 `run\` 直接 `dotnet publish`，也不要强杀进程。

## 技术栈

C# + WPF（.NET 8），Windows 10 / 11。
