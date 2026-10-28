## 1.8.26-r8-bep5421：修复八模块 Harmony 变更监听（2026-10-29）

- 修复 `Harmony mutation schema changed`：按官方 HarmonyX 2.9 的完整签名监听 11 个直接修改入口，不再匹配旧版的 3 个入口。
- 原 Mono 测试复现旧包的 8 个模块失败；新版 PackageJsonBinaryCache、MorphSnapshotPackageState、MorphClassificationReceipts、AssetAnatomyPass、PathCharacterSearch、AssetEntryMetadata、PackageNegativeFilesystemProbe、VarDirectorySelection 的对应启动入口全部通过。
- 缓存兼容检查补齐 `ILManipulators`，第三方更改方法时仍回到原逻辑；没有关闭优化模块，也没有在每帧新增扫描。监听器安装失败时撤销本次挂钩。
- 五类挂钩的新增/按方法撤销、11 个直接入口、重复安装及异常后的 busy 归零已验证；保留 r7 的注册表修复，路径/JSON 与纹理 RGB24/RGBA32 像素、UI 挂接回归通过。
- **图片加载停在 63/78 的反馈尚未复现**。已检查完成预算、解码准入、共享解码与完成通知路径，暂未取得该停滞的根因证据；本版不宣称已修复这一现象，也不强制跳过未完成纹理。保留预发布标记。

### 升级

完全退出 VaM → 覆盖解压新版（保留 `VaMAcceleration/backups`）→ 再运行 `Install_VaM_Acceleration.bat`。仅覆盖解压不会更新实际运行插件。
已有 BC7 缓存且本次不需转换时，可运行 `Install_VaM_Acceleration.bat --no-bc7`。

### 构建

`tools/build_bep5421.py` 新增必填 `--game`，指向本地 VaM 的引用目录；先重建 r7 载荷，再由 `tools/build_mutation.py` 编译当前源码 donor，仅移植明确列出的 14 个方法体。其余方法和 5 个资源保留。编译时临时移除 Obsolete 错误属性的引用不进入发行包，实际运行仍使用官方原字节 HarmonyX。
