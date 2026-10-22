# 1.8.26-r3 预设浏览器鼠标修复

## 已定位的缺陷

VaM 的 `LookInputModule.ProcessMouse/ProcessMouseAlt` 将鼠标命中写入 `currentLookMouse`，并使用 `cursorMouse`。旧 UI 只缓存左右手柄字段；r2 的桌面前缀把读取方向改成左手，仍未读取鼠标字段。浏览器卡片禁用 Unity Button 的自动点击，由 Tick 的命中／按下／释放判断统一处理，因此会出现窗体可见、卡片及目录均不响应的情况。此前桌面投影测试未覆盖这两个字段，这是 r3 补齐的回归项。

## 修改边界

- `VrPointerPresentation` 新增两个缓存访问器；仅桌面使用鼠标字段，空命中不回退到旧手柄目标。鼠标射线和 grid 平面坐标使用同一相机。
- `VrPresetBrowser.Recenter/Tick` 统一调用整体视口适配，按相机实际视口／FOV 容纳包括左栏在内的根矩形；紧凑模式仍保留左侧 dock。
- 冻结 r2 UI 只改 7 个原方法，3305 个原方法不变；新增辅助类型内嵌，不需要额外 DLL。identity/MVID 保持。`FileBrowserTakeover`、卡片分发、Commit 和原始选择回调逐方法保持。
- Memory 及编译资源、核心／Mono／Skin 差分、安装器、配置和第三方依赖字节不变。不是全量更新本机后续 UI／Performance。

## 定向验证

- r2 基线及本机当前载荷：原 Mono 夹具各复现 4 项桌面字段错误，16 个文件／16 个文件夹选择均未进入回调。
- 修订载荷、本机窄补丁及当前源码构建：各 100 项检查通过，4 类预设 × 本地／VAR × 简单／完整回调共 16 次选择及 16 次目录导航；目标捕获、路径及关闭标志一致，左右手 OVR／OpenVR 和空命中通过。
- 夹具仅替换 Unity 引擎专属的对象存活判断、时钟、UI关闭和目录显示，执行实际载荷的字段访问器／OnCardClicked／Commit；不把它当作真实 Unity 输入事件或人物加载验收。
- 108 组／1310 项投影检查，覆盖 4:3、窄／竖窗口、宽屏、FOV、紧凑布局、鼠标坐标、VR 分支；原 Mono 准备实际发货载荷的 13 个相关方法通过。
- 独立副本恢复两个基线哈希及原失效表现，候选仍保持修订。准确 ZIP 再做中文／空格路径安装／恢复与条目哈希核对。
- 未操作用户当前人物、预设、服装或场景；未做 Unity 实机桌面点击、VR 画面或新增 FPS／加载速度验收。另报 meta.json／空白预览不列为本次修复。

## 可重复构建

从 r2 ZIP 提取 UI 载荷作为 BASELINE_UI。GAME 是 VaM 根目录；OUTPUT 为新的输出目录：

```powershell
python tools/build_browser_revision.py --game GAME --baseline-ui BASELINE_UI --output OUTPUT
```

工具核对 r2 原件哈希，再编译内嵌 helper／定点修补器，并校验所有原方法、资源及 identity/MVID 的改动边界。源码和定向测试保存在 `src/browser_mouse_20261022/`。正式用户回滚入口仍为安装包内 `Restore_VaM_Acceleration.bat`。
