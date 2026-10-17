# 离线安装器 1.8.26-r1

## 执行顺序

1. 确认游戏已退出、没有未完成事务，检查原件白名单及载荷哈希。
2. 在安装器自己的临时目录建立 worker；核验 `components.json` 的 `worker` 项，复制 `runtime/worker` 私有依赖及冻结工具。
3. 只在临时目录重建目标核心 / Mono。Bootstrap 先按绝对路径加载配套 Cecil，再加载其余私有依赖，核验实际加载位置及 `DefaultReflectionImporter` 类型。
4. 在独立 Mono 进程实际执行 `InitializePortable`。`Check` 到此结束，游戏文件与缓存写入数为零；临时文件清理，诊断日志保留。
5. 安装才创建事务，部署文件和 Naturalis 差分，然后建表及执行可选 BC7 转换。
6. 任一后续步骤失败，恢复本轮事务；独立还原入口、冲突保护及原文件备份保持原逻辑。

## 为什么不覆盖 Managed 下的 Cecil

不同整合包可能保留相同程序集身份但不同内容的工具 DLL。旧 worker 直接加载依赖 Harmony 的 helper，目标 `VaM_Data/Managed` 的 DLL 可能先被解析。
新 Bootstrap 仅引用基础库，在加载 helper 之前绑定并验证私有依赖；运行游戏用的 `Managed` 不被拿来“修复”安装工具。
仅调整搜索顺序没有提供相同的预加载及实际位置核验保证。

## 日志与还原

- `VaMAcceleration/logs/日期-进程号.log`：包括预检失败在内，记录 worker 命令、输入、退出码、stdout、stderr。
- `VaMAcceleration/backups/日期-进程号/`：正式安装的备份、事务及 worker 日志。
- 出现 `WORKER_DEPENDENCY_DAMAGED`：重新完整解压当前 Release；不要从其他程序寻找同名 DLL。
- 出现 `WORKER_FAILED`：提交本轮日志。预检失败发生在安装事务前，后续失败则查看 `FAILURE_ROLLBACK` 或 `ROLLBACK_PENDING`。
- 还原继续使用 `Restore_VaM_Acceleration.bat`；升级解压时保留旧 backups。

## 构建与测试

构建 Bootstrap：`python tools/build_worker.py --game "X:\VaM"`。配套依赖来自冻结包的 `payload/BepInEx/core`，不从临时安装的其他版本拼装。

`tools/test_installer.py` 覆盖事务及全局进程保护；`tools/verify_release.py` 使用空 `VaM.exe` 标记的独立副本运行真实 Mono、建表、BC7 及副本还原。
测试专用 `tools/run_fixture.py` 仅在目标是空标记时跳过全局游戏进程检查，不进入安装 ZIP；正式安装器仍检查实际 VaM 进程。
