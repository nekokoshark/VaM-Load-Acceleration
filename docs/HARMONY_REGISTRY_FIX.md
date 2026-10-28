## 1.8.26-r7-bep5421：修复 Harmony 注册表绑定（2026-10-28）

修复 r6 在官方 BepInEx 5.4.21 / HarmonyX 2.9 下，`CanonicalPathsRuntime` 与 `JsonCapacityRuntime` 查找不存在的 `HarmonyLib.Internal.GlobalPatchState` 导致初始化失败的问题。绑定改为此版本真实使用的 `HarmonyLib.Public.Patching.PatchManager.ToPatchInfo`，保留后装补丁的回退检查，无新增逐帧扫描。

**升级：退出 VaM → 覆盖解压本包（保留 `VaMAcceleration/backups`）→ 重新运行 `Install_VaM_Acceleration.bat --no-bc7` → 看到 `INSTALL_PASS`、退出码 0 后启动。** 仅解压不会替换已安装的插件 DLL。也可直接双击安装 BAT 完整执行。

两处原始类型加载错误已在原 Mono 复现；新版初始化、20 轮外来补丁挂接/撤销、原生路径与 JSON 回退、7 模块和缩略图像素、完整安装/独立还原通过。此前 r6 的 7 模块测试漏测了这两条初始化路径，本版补上该回归。

本版仅确认修复上述启动错误；反馈中的整场景灰白人物/预览空白尚需对方升级复测，没有将离线像素测试表述为实景画面验收。保留测试版标记；未关闭功能掩盖错误，未替换皮肤、预设或第三方资源。
