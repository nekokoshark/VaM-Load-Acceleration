# r4：兼容运行时更新、清理过的派生缓存

## 原因与修订

旧还原器将所有文件按静态安装载荷校验。运行中更新或清理派生缓存后，还原会报 `RESTORE_CONFLICT`，阻塞后续安装。此次按安装器实际生成/转换的格式逐类处理，而非只修截图中的mri。

- 覆盖5类：`VaMMemory/MorphImports/imp-<SHA256>.mri`、`ClassificationReceipts/<SHA256>.mcr`、`PackageTrees/<SHA256>.vjt`、`PackageJSON/<包UID>.vamcachejson/meta`、缓存根目录中的BC7 `_C.vamcache/meta`。目录、名称和安装事务记录均需匹配。
- 对变化的已记录派生缓存，先将当前内容和时间戳另存到这次备份的 `preserved-cache`，核对哈希、保存记录后，再按原安装记录还原；原备份也保留。日志打印 `RESTORE_CACHE_SAVED`。已清掉的旧缓存按原备份恢复，安装中新建的缓存正常移除。
- 不直接把缓存冲突改成“跳过”：PackageJSON和BC7数据/元信息都按原记录还原，避免混用新旧文件；此前老工具已经还原了一半的文件对也可继续收尾。
- 缓存留存写入使用临时文件并核对哈希后替换，复制失败不覆盖当前缓存；完成留存后发生中断可重试，已保存的相同版本复用，不重复堆积。
- DLL、配置、VAR、其他路径及符号链接继续报告冲突，不忽略或覆盖后来修改的内容。
- 修正空的未完成事务：还原会将其收尾，避免 `RESTORE_PASS files=0` 后仍被 `INTERRUPTED_TRANSACTION` 阻塞。

## 当前被卡住的用户

1. 退出 VaM，将 r4 ZIP 内文件覆盖解压到原游戏根目录，保留现有 `VaMAcceleration/backups`。
2. 运行 `Restore_VaM_Acceleration.bat`，确认 `RESTORE_PASS` 和 `Exit status: 0`；遇到变化的已记录派生缓存会先打印 `RESTORE_CACHE_SAVED`。
3. 再运行 `Install_VaM_Acceleration.bat`，等待 `INSTALL_PASS` 和 `Exit status: 0` 后启动 VaM。

若仍出现其他文件的 `RESTORE_CONFLICT`，保留错误输出和备份记录，按具体冲突项排查。

## 验证边界

原版在同一组文件夹具中复现缓存冲突及空事务残留；修订通过12项新增恢复回归和7项原有安装器测试（新增测试内含多组参数用例）。覆盖外部缓存根目录、默认/相对/绝对缓存设置、中文空格路径、五类缓存、元信息配对、旧工具部分还原、中断续恢复、备份失败、重复还原、普通文件冲突及真实符号链接。准确发行ZIP使用自带Python再次执行12项测试，并验证命令行恢复及空事务收尾；所有ZIP成员重读核对哈希。

r4只更新安装器恢复代码和发行记录。r3的UI、Memory、核心/Mono/Skin差分及其他运行文件字节不变；未操作反馈者机器，未增加Unity交互或性能验收结论。
