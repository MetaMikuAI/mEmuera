# mEmuera

.NET 10 ERB 游戏引擎，支持终端与 Android，优先兼容 EraTW。

资源按需读取，脚本索引自动缓存在数据目录的 `cache/index/`。脚本、配置或引擎改变后自动重建；也可删除该目录强制重建。函数体在调用时编译，修改游戏文件后请重新进入游戏。

## 终端版

使用 `global.json` 指定的 SDK：

```powershell
dotnet build mEmuera.sln -c Release
dotnet run --project src/mEmuera.App.Tui -c Release --no-build -- --game "游戏目录"
```

## Android

将游戏文件直接放入 `/sdcard/emuera/`，ERB、CSV 目录就在其中，存档也保存在这里。授予“所有文件访问权限”后打开游戏。

上下拖动查看历史，双指缩放字号，≡ 菜单提供存档与读档。暂无音频。

构建需 .NET Android 工作负载、Android SDK 和 JDK：

```powershell
dotnet build src/mEmuera.App.Android -c Release -p:AndroidSdkDirectory="Android SDK 路径" -p:JavaSdkDirectory="JDK 路径"
```

[自动构建与发布](docs/发布.md)：推送检查，版本标签发布。

## 许可证

[GNU GPL v3.0](LICENSE)
