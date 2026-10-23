# r5：配置自动重写后的还原兼容性

截图中的 `RESTORE_CACHE_SAVED` 表示 r4 已完成缓存留存；下一项 `VaM.Memory.cfg` 发生整文件哈希冲突。BepInEx运行时会补默认设置、生成注释或调整格式，用户也可能改动设置；截图只确认文件变化，不确定具体改动来源。

## 修复行为

- 仅处理安装器实际管理的 `BepInEx/config/VaM.Memory.cfg`、`doorstop_config.ini` 和 `PerformancePatches/SkinMeshPartDLL.ini`，不是忽略所有cfg冲突。
- 使用原备份、安装时内容、当前配置三方比对。仅撤销安装器确实改动、且当前仍为安装值的设置；用户后改的值、删掉的键、其他设置及运行时新增默认项保持。原配置文件未改动时仍精确还原原字节与时间戳。
- 变化配置完整内容和时间戳先保存到当前事务的 `preserved-config`，核对哈希后才合并。原 `backup` 及 r4的 `preserved-cache` 保留。
- 新安装事务保存 `installed-config` 原样副本。旧 r2/r3/r4 记录按原写入规则重建，并要求SHA256精确匹配记录的安装值；无法识别的安装内容继续报 `RESTORE_CONFIG_BASE_UNKNOWN`。
- 支持UTF-8 BOM、CRLF、BepInEx布尔值大小写、中文/空格路径、中断后重试和r4已经还原部分文件的事务。格式损坏、重复键、损坏备份、符号链接及未知配置仍会中止并保留现有内容。
- r4五类缓存兼容性保持；所有插件DLL、核心/Mono/Skin差分及运行载荷保持r4字节不变。

## 当前被卡住的用户

1. 退出VaM，把r5 ZIP覆盖解压到原游戏目录；保留整个 `VaMAcceleration/backups`。
2. 先运行 `Restore_VaM_Acceleration.bat`，等待 `RESTORE_PASS` 和 `Exit status: 0`。变化配置会打印 `RESTORE_CONFIG_SAVED` / `RESTORE_CONFIG_MERGED`；中断续收尾可能打印 `RESTORE_CONFIG_RESUMED`。
3. 再运行 `Install_VaM_Acceleration.bat`，等待 `INSTALL_PASS` 和 `Exit status: 0` 后启动VaM。

已有r4部分还原记录可以继续使用，不删除或编辑state.json，不手动覆盖冲突配置。

## 验证

19项配置回归、12项缓存回归、7项原有安装器测试通过，包含真实临时符号链接负控。准确ZIP以自带Python重复验证，并从r4已还原缓存后停在配置冲突的事务继续还原。独立安装器副本回滚恢复原hash和原冲突行为。反馈者机器实际升级结果待复验。
