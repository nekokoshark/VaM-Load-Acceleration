# BepInEx 5.4.21 适配测试版

目标为用户确认的 **VaM 1.22.0.13**。旧清单曾标注 1.22.0.12；本次更正标注，但不因版本文字放宽核心、Mono、Skin 的哈希检查。

## 修正内容

- 游戏内统一使用官方 BepInEx x64 5.4.21.0、HarmonyX 2.9.0.0、MonoMod 22.1.29.1 及配套 Doorstop。完整引导文件指纹列在 components.json。
- Memory 的 175 处 `Harmony.Patch` 调用改用新版返回签名，4 处 `AccessTools.Constructor` 调用补上 `searchForStatic=false`。所有旧 Patch 返回值均原本被丢弃；不改变其业务逻辑。
- Memory、UI、HotLoader 的引用明确指向实际运行版本，避免被自动转写到 `0Harmony20`。保留兼容桥供其他旧插件使用，不全局关闭 HarmonyInterop。
- 安装器接受原配套 5.1 或官方 5.4.21 的一致引导组合；升级时事务备份，未知或混装引导仍在写入前退出。
- 离线建表 helpers 和原版 Memory 留在独立 Mono 进程，继续使用私有 5.1 依赖。它们不会作为第二份游戏插件运行。
- r5 的配置三方还原、缓存还原及原有性能差分保持；未混入本机后续功能。

## 安装

正常退出 VaM，将发行 ZIP 内的文件解压到游戏根目录，**保留 VaMAcceleration/backups**。先运行 `Check_VaM_Acceleration.bat`，再运行 `Install_VaM_Acceleration.bat`。不要只手动替换一个 BepInEx.dll，也不要将旧 DLL 混回 core。

恢复使用 `Restore_VaM_Acceleration.bat`；每次还原最近一笔未还原事务。安装前已有 5.4.21 时恢复其原字节，原为配套 5.1 时恢复 5.1。保留原启动 BAT，安装工具不启动游戏、不改 VR 链路。

## 验证结果与边界

- 原 Mono 独立进程复现：旧 Memory 直接绑定新 Harmony 时抛 `MissingMethodException: HarmonyLib.Harmony.Patch`。
- 官方 HarmonyInterop 的真实转换也已执行：旧 Memory 出现报告中的 `Shimming ... (0Harmony20)`，新版不触发该转换。
- 注意：**旧 Memory 经官方 shim 后，下面的七组定向测试也能通过**。因此转换日志本身不是故障证据，也没有把直绑故障等同于报告中全部空白／卡住的已证实根因。
- 新版 2,751 处外部成员引用解析无缺失；七组纹理／形变模块实际安装成功（26 个 patch 条目），原生形变构造、形变库补丁与清理通过。
- 实际解码 RGB24／RGBA32 缩略图并核对像素；UI 的真实 Harmony 包装器完成挂接及还原。
- 完整安装、BC7 编码解码、形变建表、重复安装、注入失败恢复、独立副本还原通过；6 项引导组合测试及原有 38 项事务／缓存／配置测试通过。
- 尚未在报告人的 Unity 整场景与完整第三方组合复现并验收空白缩略图／174 条 invalid path。此版作为适配测试版发布，不声称 FFNR 不兼容，也不要求清空用户贴图缓存。

## 可复现构建

在本仓库执行（Windows .NET Framework 4.x）：

```powershell
python tools/build_bep5421.py --baseline-app D:/r5/VaMAcceleration --upstream-zip D:/BepInEx_x64_5.4.21.0.zip --output-app D:/build5421/VaMAcceleration
```

基线取公开 r5 安装包。工具先验证基线和官方 ZIP 的完整哈希，输出必须为新目录；修改仅涉及上述 ABI／引用及安装器，其他差分保持原样。工具源码为 `tools/Retarget5421.cs`。官方 ZIP SHA-256：`2af69fe0aaf821e95c4cd3e4d53860e667c54648f97dca6f971a5bcd3c22aa34`。
