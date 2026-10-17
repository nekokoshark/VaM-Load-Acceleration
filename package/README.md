# VaM 加载优化补丁

统一打包已部署的 **VaM.Memory 1.8.26 + Quest3TriggerUI 4.6.302** 场景与人物加载加速工作，提供 Windows x64 离线安装器、建表工具、BC7 转换和备份还原。

## 下载与使用

从 [Releases](https://github.com/nekokoshark/VaM-Load-Acceleration/releases) 下载 `VaM_Load_Acceleration_1.8.26-r1.zip`。GitHub 的自动 Source code ZIP 是源码，不是安装包。

**1.8.26-r1 修复安装器依赖加载**：离线 worker 固定配套 Cecil / Harmony / MonoMod 等 12 个 DLL，在改动游戏文件前完成实际加载预检，解决目标 Managed 中同名 DLL 抢先加载引发的 `DefaultReflectionImporter` 错误。运行时仍为原冻结版本；见[更新记录](CHANGELOG.md)及[安装器设计与日志](docs/INSTALLER.md)。

1. 正常退出 VaM。
2. 把安装包**里面的文件**解压到 VaM 根目录。`Install_VaM_Acceleration.bat` 与 `VaM.exe` 应当在同一层。
3. 可先双击 `Check_VaM_Acceleration.bat`：检查兼容性并实际预检 worker，不写游戏或缓存，诊断日志保存在 `VaMAcceleration/logs/`。
4. 双击 `Install_VaM_Acceleration.bat`，等到 `INSTALL_PASS` 与 `Exit status: 0`。
5. 使用自己的原启动 BAT 启动 VaM。安装器不启动、关闭或操作游戏，不改 VR 启动链。

安装器自带 Python，不需要安装系统 Python，不需要联网。需要 Windows x64 及系统 .NET Framework 4.x。首次全库预建和 BC7 编码可能较久；再次执行用于补齐更新后的包库。

命令行只建表、不做本轮 BC7 扫描：

```bat
Install_VaM_Acceleration.bat --no-bc7
```

`--no-bc7` 只跳过离线转换；运行插件原有的按需转换开关仍由 `VaM.Memory.cfg` 控制。

## 兼容范围

- 目标游戏：**VaM 1.22.0.12**。核心、Mono、Skin DLL 使用 SHA-256 白名单差分重建；仅版本号相同不代表兼容。
- 需要已有匹配的 **v13beta1 系列 Skin 性能补丁原件**。包内有我们的 Skin 改动差分，而没有第三方完整原件。已有相同最终版本也可直接使用。
- 支持的原始／中间核心、Mono、Skin 哈希全部列在 [`components.json`](VaMAcceleration/components.json)。未知原件在任何安装写入之前退出，输出 `UNSUPPORTED_BASE`。
- 已有 BepInEx／Doorstop 需要与包内引导依赖兼容；不强行替换不同的引导器。重复的旧 Q3 普通载荷会被拒绝。
- Quest3TriggerUI 的载荷文件必须保留 `.payload.dll.disabled` 后缀，由配套 HotLoader 加载。不要另放一个普通 `.payload.dll`。
- 兼容性检查通过不等于实机画面、物理及插件组合已验收。请在自己的常用场景核对。

## 包里不只是缓存工具

| 方向 | 已包含的改动 |
|---|---|
| 场景 UI / BrowserAssist | 加载期 UI 重同步合并、包库扫描增量、VAR 名称／条目索引、块流读取 |
| 人物预设 | **QuestTriggerUI 人物／外观／服装预设加速也包含在内**：差量应用、实例与准备结果复用、头发渲染批处理、重复清扫合并和有边界的 GC/UUA 调度 |
| 形变建库 | 公共纯元数据会话复用、男女全库导入收据、原导入／过滤／激活链保留、小形变流式分类、跨人物内存收据与磁盘分类表、首次标量索引 |
| JSON 与文件查询 | 场景及包 JSON 流式解析、原生包 JSON sidecar、经完整源哈希检查的二进制 JSON 树、不可变字符串／描述表、路径规范化、目录选择和负查询复用 |
| 资产创建 | 非活跃 Anatomy 分支、条目元数据与文件查询热路径、准备资源所有权转交、不可变委托图共享、避免克隆历史被订阅根长期保留 |
| Bundle / 依赖 | 外部 worker 的 Bundle 缓存准备、加载窗口预算与预加载租约、取消／失败依赖的释放、BOM 协议修复；资产真正的 Unity 创建和依赖完成条件不被假跳过 |
| 插件编译 | 原 Mono 外部编译 worker、字节码模板缓存；各插件实例及程序集静态状态仍隔离 |
| 贴图 | 缓存流读写、上传与解码复用、并行请求合并、内存准入与写盘预算、暂存生命周期、合格色图 BC7 转换和读取兼容性 |
| 配套运行时 | 已部署的 Mono 分配／释放改动及 Skin 访问／准备链改动，以差分形式安装；相关资源退出与加载收尾合并逻辑仍在统一 DLL 中 |
| 可选 Naturalis 源码补丁 | 对 15/43/44/51/53/54/55/70/72/73/92/93 已核验源版本，按精确源哈希只替换目标源码条目；没有安装相应版本就跳过，未识别源码不改 |

Naturalis 补丁不包含原 VAR 或第三方资源。重建 VAR 时保留其他条目的内容、名称、元信息和顺序；原件先备份。ZIP 的压缩字节可因重建发生变化，不以整个 VAR 字节相等作为未改资源的判据。

**不包含**未部署的 1.8.27 微候选、实验性插件 UI 重写、跳过必需依赖、减少人物／服装／物理或偷偷降画质。BC7 本身是有损编码，不是像素无损转换。

## 缓存目录和更新

从目标根目录的 `prefs.json` 读取 `cacheFolder`。绝对路径直接使用，相对路径相对于该 VaM 根目录；缺省为 `Cache`。不把作者本机的 `D:\vamcache` 写死进安装器。

例如用户自己的配置是 `D:\vamcache`，则输出：

```text
D:\vamcache\PackageJSON\                 # VaM 原生包 VMI/VAM JSON sidecar
D:\vamcache\VaMMemory\MorphImports\      # 男女纯元数据导入表
D:\vamcache\VaMMemory\ClassificationReceipts\
D:\vamcache\VaMMemory\PackageTrees\      # 原生包 JSON 的二进制树
D:\vamcache\VaMMemory\StartupMorphIndex\
D:\vamcache\VaMMemory\BundleDiskCache\
D:\vamcache\VaMMemory\CompilerBytecode\
```

前四类可由 BAT 在目标机器预建。其余按首次实际请求生成；不是提前启动所有场景或创建所有 Unity 对象。Bundle、编译结果、贴图与本地散装资源的按需路径仍在运行组件中；未覆盖／被拒绝的离线输入继续走原加载路径。

包库递归扫描支持链接目录并检测环，重名 UID 不猜选其中一个。形变表记录目标路径、源版本／时间戳、文化设置及程序集 MVID；JSON 树校验完整源内容和载荷。变化的来源使对应收据失效，重新加载／重跑 BAT 更新。**没有宣称文件一变就全库实时重建**。

不会按容量自动清理这些子缓存，也不会扫描并删除用户原生贴图。新场景、人物、服装只在实际访问了相应数据后生成其适用缓存；某项没有可复用数据时不会凭空生成完整场景快照。

## BC7 的资格与备份

只扫描原生 `*_C.vamcache` 色图：RGB24 或所有 mip 均不透明的 RGBA32，支持的完整／单级 mip 链，256 KiB–256 MiB，尺寸 1–16384。normal、bump、mask、线性用途和透明色图不走这条转换路径。非 POT 尺寸按兼容工具规划；不能节省实际字节就保留原生格式。

每项均经过源检查、编码、解码后不透明检查、长度验证；**原数据和元数据成对备份，带持久事务记录，再提交**。有损压缩可能改变颜色细节。新色图运行时继续按已有请求条件处理；重新执行安装 BAT 也可转换之后生成的合格原生缓存。

## 还原

正常退出 VaM，然后双击：

```bat
Restore_VaM_Acceleration.bat
```

每次安装的原文件、SHA-256、mtime、提交状态和 worker 日志放在 `VaMAcceleration\backups\日期-进程号\`。还原最新未还原事务，可重复执行逐层撤销之前的安装。首次安装能还原核心／运行组件、配置、改过的 VAR 和 BC7 原始数据／元数据；新建的事务缓存文件也会撤销。

失败自动回滚；被中断的事务可由同一个还原入口继续处理。工具每次提交／还原前检查 VaM 进程，不杀进程。之后若游戏或用户修改过同一个文件，会报 `RESTORE_CONFLICT`，保留后续修改，不静默覆盖。请保留 backups，尤其 BC7 的原贴图备份可能较大；此版本不自动删备份或缓存。

## 测试与收益口径

本次发布包在独立中文／空格路径副本通过：原始基线检查、完整安装与建表、真实 BC7 编码／解码、不透明／透明／normal 区分、Naturalis 精确源码条目修改、独立副本哈希还原、重复安装不改源时间戳、未知核心拒绝、worker 故障的事务回滚。建出的表另外由冻结 DLL 的原入口回放。

既往本机 Westo 手估时长存在约 170→115 秒的趋势，但不是每项优化的严格独立贡献。最终内部阶段记录约创建 44.6 秒、延迟恢复 22.3 秒；内部 `LoadPerf` 不等于用户可操作全程。**本发布是已有部署的统一分发，不承诺任意包库稳定再省固定 10/20 秒**。实机组合与更多设备仍需验证。

## 开发与来源

- `package/`：与 Release ZIP 对应的可安装文件。
- `src/memory/`：冻结 Memory 编译输入与嵌入资源；`build.json` 不使用本机绝对路径。
- `src/offline/`、`src/bc7/`、`src/native/`：便携建表、兼容检查和独立 Mono host 源码。
- `tools/`：定向回归、构建和打包工具。
- [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)：第三方组件、来源与随包许可证。
- 完整 QuestTriggerUI 工程见 [Quest3TriggerUI 仓库](https://github.com/nekokoshark/Quest3TriggerUI)；本包冻结的是上文哈希所对应的载荷，不自动追随其后续版本。

重编译运行 DLL 会改变 MVID，原机器生成的形变表不能当作新 DLL 的有效表。重新建表再验证，不用旧表证明新构建兼容。
