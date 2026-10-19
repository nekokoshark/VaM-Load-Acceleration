# 更新记录

## 1.8.26-r2 — 桌面适配与 BA 编译协议（2026-10-19）

- 预设浏览器按实际桌面相机视口／FOV 缩放和定位，修正左侧裁切，兼顾 4:3、窄窗口与宽屏；桌面指针改用鼠标射线，OVR／OpenVR 原分支保留。
- 同步适配编辑器、收藏／预设／场景侧栏、禁用／锁定／服装锁／编辑器切换按钮、表情面板及原生浏览器工具栏；关闭编辑器时恢复其原姿态和缩放。
- 在载入 Mono 前隔离普通 stdout，将编译二进制回复写入专用句柄；修复 managed Console、Win32 或 CRT 诊断污染回包造成的 `Compiler reply magic`，不扫描魔数或吞掉解析异常。
- 仅更新 UI 4.6.302 载荷及 Memory 1.8.26 的两个编译嵌入资源；Memory 方法、字段、identity／MVID 保留。核心／Mono／Skin 差分、配置、安装器与 r1 私有依赖保持。
- 108 组布局／1310 项检查、原 Mono JIT、真实 BA91 编译及独立副本回滚通过。桌面布局使用确定性投影夹具，尚未做 Unity 实机桌面交互验收；诊断污染是注入复现，不代表已取得报告者原始污染文本。
- 另报的 `meta.json not found`／预览图空白问题暂缓，本版不宣称解决。

升级：退出 VaM，将新 ZIP 内文件解压覆盖到同一根目录，保留 `VaMAcceleration/backups`，运行检查和安装 BAT。运行版本号仍是冻结基线，以发行修订号及 `components.json` 哈希区分 r2。需要还原时退出 VaM 后运行 `Restore_VaM_Acceleration.bat`。

## 1.8.26-r1 — 安装器依赖隔离（2026-10-17）

- 离线 worker 自带并核验 12 个配套 BepInEx / Harmony / MonoMod / Cecil DLL；先加载私有依赖，再载入建表工具，不再借用目标 `Managed` 下的同名工具 DLL。
- 检查及安装均先在临时目录重建目标核心 / Mono，并实际运行依赖探测和 `InitializePortable`。通过后才创建安装事务和改动游戏、VAR、缓存。
- 保留每次 worker 的命令、标准输出、标准错误和退出码到 `VaMAcceleration/logs/`；后续建表失败仍自动回滚。
- 修复范围仅为分发和安装器。冻结的 Memory 1.8.26、Quest3TriggerUI 4.6.302、核心 / Mono / Skin 差分及 Naturalis 差分不变。

升级时退出 VaM，将新 ZIP 内文件解压覆盖到同一根目录，保留 `VaMAcceleration/backups`，然后运行检查和安装 BAT。不要只替换 `installer.py`。
