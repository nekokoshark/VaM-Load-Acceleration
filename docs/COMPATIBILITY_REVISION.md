# 1.8.26-r2 兼容修订

本版只修复冻结分发包的桌面 UI 和编译回复通道；不部署本机 Memory 1.8.33、UI 4.6.325 或 Performance 0.7.85。

## 修改边界

- UI：新增内嵌桌面布局辅助类型，16 个原方法挂钩；3296 个原方法中 3280 个保持，资源不变，无额外 helper DLL 依赖。通过 `!isOVR && !isOpenVR` 判断桌面，不以头显是否连接判断。保留 world-space Canvas，不把原 HUD 改为 Overlay。
- 桌面按 lookCamera 视口和 FOV 容纳浏览器及编辑器／侧栏；紧凑选择器给左侧 dock 留位。UIAssist 根矩形默认 100×100，按直接可见控件边界计算。鼠标使用 `ScreenPointToRay`；编辑器 teardown 恢复原姿态和缩放。
- 编译 runner：加载 Mono 前复制原 stdout 二进制管道句柄，CRT stdout 和 Win32 标准输出重定向 stderr；worker Main 唯一回复流从 `VAM_COMPILER_REPLY_HANDLE` 打开专用 `FileStream`。
- Memory：仅替换 `VaM.Memory.CompilerWorker`／`VaM.Memory.CompilerRunner` 两个嵌入资源；2412 个方法、3065 个字段及其他 3 个资源不变，identity／MVID 保留。worker 32 个方法中仅 Main 回复流改变。
- 不修改 CompilerService、回包格式、缓存策略、文件系统开关、核心／Mono／Skin 差分或其他运行载荷。

## 可重复构建

从 r1 安装 ZIP 提取下列两个载荷作为 BASELINE_UI 和 BASELINE_MEMORY；GAME 是包含原 Mono／Managed 引用的 VaM 根目录，OUTPUT 必须是新目录。Windows .NET Framework 4.x 编译器与 Python 3.12 即可运行：

```powershell
python tools/build_compatibility_revision.py --game GAME --baseline-ui BASELINE_UI --baseline-memory BASELINE_MEMORY --output OUTPUT
```

工具先核对两原件的 SHA-256，再进行定点 IL 修补及合同／布局测试。输出是发布载荷，不重新编译整个冻结程序集，不改变其 MVID。

| 载荷 | r1 SHA-256 | r2 SHA-256 |
|---|---|---|
| Quest3TriggerUI.payload.dll.disabled | 3dd26fac2e59ce5428bd23fa4d8453676a48ebe8ce5c5f7d082d17d3489f2e83 | ef80141a4b84a0f05d977c60fb48e6979ee391aa1fccda2110c1a371dc855c14 |
| VaM.Memory.dll | 213956eed3e3869254622b44473b635a26932b7321f7ceec185cf1e5d9a75bb8 | 5533222292db19c8cb0b8a5d8cb66de70e5a6ada31559341ae807a4c8e91b47a |

## 验证与边界

- 基线：6 种分辨率／窗口投影复现原固定比例裁切；真实 BA91 清洁请求通过，注入 managed／Win32／CRT stdout 后原解析器报 `Compiler reply magic`，退出 70。
- 修改：108 组布局、1310 项检查覆盖 viewport、窄窗口、FOV、鼠标射线、编辑器恢复和 VR 分支保持；游戏原 Mono 准备 31 个相关方法通过。真实 BA91 五次编译覆盖清洁回复、三类噪声隔离、缓存与 worker 回收；另验证语法错误回复和回包无尾随字节。
- 回滚：在独立副本恢复 UI、Memory、worker、runner 四原件哈希，原固定比例及原噪声错误恢复；修改版工件仍保持。
- 便携构建输出与测试载荷逐字节相同；安装器与准确发行 ZIP 再做中文／空格路径检查、安装、真实 BC7、Naturalis、重复安装及恢复回归。
- 没有启动／重启 VaM，也没有换入本机运行插件。布局验证不是 Unity 画面或用户鼠标操作验收；未新做 VR 或速度收益测试。报告者机器的原始 stdout 文本尚未取得，注入污染只证明该协议缺陷及其修复。
- `meta.json not found`／预览图空白问题不属于本版已解决事项。

正式安装／恢复仍使用包内 BAT；本地测试用 ROLLBACK.sh 只恢复测试副本，不是用户卸载入口。旧 r1 Release 保留。
