# 定向测试与构建

Windows x64；Python 3.12，系统 .NET Framework 4.x。所有测试在副本执行，不启动 VaM。

```powershell
python tools/test_installer.py
python tools/build_memory.py --game "C:\VaM" --output ".build\VaM.Memory.dll"
python tools/make_zip.py --output ".build\VaM_Load_Acceleration_1.8.26.zip"
```

build_memory 使用目标 VaM 的引用，生成的是新 MVID，不自动替换正式载荷，也不让旧表假命中。

完整端到端测试还需要用户自己提供的原件：测试基线目录放置受支持的 `Assembly-CSharp.dll`、`mono.dll`、`SkinMeshPartDLL.dll` 和 Naturalis 55 原版 `MorphListener55.cs`；它们不分发在仓库内。

```powershell
python tools/verify_release.py --game "C:\VaM" --baselines "C:\TestBaselines" --work ".build\e2e"
```

测试创建中文空格目录、合成 VAR/VMI/VMB 和色图，复制本地 Managed/Mono 引用后执行安装。原件、原路径与实际游戏保持不变。`--work` 每次使用全新的测试目录；脚本不会删已有目录。

`src/offline` 里的 C# 工具用目标 Managed/Core 引用与冻结 Memory 载荷编译；最小分类器必须由目标 VaM 的 `DynamicCSharp.Compiler.McsCompiler` 编译，而不是直接用 Microsoft csc。否则 IL 指纹不同，建表应明确失败。`ProfileCompile.cs` 保留了这个真实编译入口。

源码快照中既有大文件保持冻结，不为格式整理改变已验证运行行为；安装器后续可按事务、建表、BC7 三个职责拆分。第三方／生成源码不作为新算法变更验收。
