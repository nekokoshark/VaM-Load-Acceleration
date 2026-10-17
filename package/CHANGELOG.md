# 更新记录

## 1.8.26-r1 — 安装器依赖隔离（2026-10-17）

- 离线 worker 自带并核验 12 个配套 BepInEx / Harmony / MonoMod / Cecil DLL；先加载私有依赖，再载入建表工具，不再借用目标 `Managed` 下的同名工具 DLL。
- 检查及安装均先在临时目录重建目标核心 / Mono，并实际运行依赖探测和 `InitializePortable`。通过后才创建安装事务和改动游戏、VAR、缓存。
- 保留每次 worker 的命令、标准输出、标准错误和退出码到 `VaMAcceleration/logs/`；后续建表失败仍自动回滚。
- 修复范围仅为分发和安装器。冻结的 Memory 1.8.26、Quest3TriggerUI 4.6.302、核心 / Mono / Skin 差分及 Naturalis 差分不变。

升级时退出 VaM，将新 ZIP 内文件解压覆盖到同一根目录，保留 `VaMAcceleration/backups`，然后运行检查和安装 BAT。不要只替换 `installer.py`。
