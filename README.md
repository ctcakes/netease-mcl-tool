# Netease MCL Tool

面向 Minecraft BJD 玩家的一键启动助手，基于 WinUI 3 和 Windows App SDK。

## 功能

- 管理员权限启动
- 优先通过 Everything ES 快速查找 `WPFLauncher.exe`
- 校验启动器和 `Mcl.Core.dll` 的 MD5
- 内嵌并释放 `es.exe`、`injector.exe`、`Mcl.Core.dll`、`MinecraftProxy.dll`
- 自动缓存启动器路径，下次启动重新验证
- 补丁、启动、进程状态监控和一键退出
- Windows 主题与 Per-Monitor V2 DPI 适配

## 构建

需要 .NET 8 SDK、Windows 10 19041 SDK 和 Windows App SDK 1.6。

```powershell
dotnet publish .\MCL.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true
```

发布文件位于 `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\MCL.exe`。

`es.exe` 是 Everything 官方命令行组件，项目构建时作为嵌入资源打包。
