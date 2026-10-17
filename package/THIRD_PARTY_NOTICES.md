# 组件与来源

本工程不是 VaM 或第三方作者的官方发行。补丁不包含 VaM.exe、UnityPlayer、完整游戏 Managed 库、人物、场景、服装、VAR 原包、用户配置或原生贴图库。

| 组件 | 来源／说明 |
|---|---|
| VaM 核心与 Mono／Skin | 由用户本机的已识别原件应用差分重建。原版权保持；不包含其完整原 DLL。Mono 历史源码见 [mono/mono](https://github.com/mono/mono)，Unity 分支见 [Unity-Technologies/mono](https://github.com/Unity-Technologies/mono)。 |
| Skin 性能原件 | turtlebackgoofy / Evo 的 VaM 1.22.0.12 v13beta1 系列性能补丁；此包只提供我们的修改差分，保留目标现有 INI。 |
| VaMLeanCore | 原工作集基于 VaMLeanCore 的现有核心；差分针对 components.json 中的明确原件，不分发完整核心或其原插件包。 |
| Quest3TriggerUI | [nekokoshark/Quest3TriggerUI](https://github.com/nekokoshark/Quest3TriggerUI)，4.6.302 冻结载荷、HotLoader；Memory 已统一实现的模块没有再单独打成第二个插件。 |
| Naturalis | everlaster；只分发经过回归的具体源码改动差分和最小分类算法工具，不附原插件资源。用户自己的匹配原件才会修改。 |
| BepInEx | [BepInEx/BepInEx](https://github.com/BepInEx/BepInEx)，MIT；见 licenses/BepInEx.txt。 |
| HarmonyX、兼容桥 | [BepInEx/HarmonyX](https://github.com/BepInEx/HarmonyX)；见 licenses/HarmonyX.txt。引导文件保持与测试环境相同的组合。 |
| MonoMod | [MonoMod/MonoMod](https://github.com/MonoMod/MonoMod)；见 licenses/MonoMod.txt。 |
| Mono.Cecil | [jbevain/cecil](https://github.com/jbevain/cecil)；见 licenses/MonoCecil.txt。 |
| UnityDoorstop | [NeighTools/UnityDoorstop](https://github.com/NeighTools/UnityDoorstop)；见 licenses/UnityDoorstop.txt。 |
| texconv | [microsoft/DirectXTex](https://github.com/microsoft/DirectXTex)，MIT；见 licenses/DirectXTex.txt。 |
| Python 3.12 嵌入版 | [python.org](https://www.python.org/)，含原 LICENSE.txt 与 Windows 嵌入发行的运行组件。运行依赖保持原许可证。 |
| python-lz4 / LZ4 | [python-lz4/python-lz4](https://github.com/python-lz4/python-lz4)；许可证见 licenses/LZ4.txt。随 Memory 支持资源使用。 |

`licenses/Mono.txt` 是 Mono 上游各目录许可证索引；差分没有把上游全文源库作为我们的原创发布。游戏内 SharpZipLib／Unity／DynamicCSharp 引用由目标 VaM 提供，未打包。

安装器 1.8.26-r1 的 `runtime/worker` 私有依赖是冻结 `payload/BepInEx/core` 中配套 DLL 的相同字节副本，沿用上表来源及对应许可证，不借用目标 Managed 中的同名工具库。

原项目、上游作者与本工程新增的安装／建表工具分别保留自己的著作权。没有把整个补丁包或第三方改动重新声明为同一个原创许可证。

Python 的原生 OpenSSL、libffi 及 LZ4 库分别保留 `licenses/OpenSSL.txt`、`licenses/Libffi.txt`、`licenses/LZ4-native.txt` 中的上游许可证。
