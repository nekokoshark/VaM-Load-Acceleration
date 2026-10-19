"""Build r2 from pinned r1 payloads without changing their assembly identity/MVID."""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import shutil
import subprocess

REPO = Path(__file__).resolve().parent.parent
SOURCE = REPO / "src/compatibility_20261019"


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game", type=Path, required=True)
    parser.add_argument("--baseline-ui", type=Path, required=True)
    parser.add_argument("--baseline-memory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    game, ui, memory, output = (p.resolve() for p in (
        args.game, args.baseline_ui, args.baseline_memory, args.output))
    assert sha(ui) == "3dd26fac2e59ce5428bd23fa4d8453676a48ebe8ce5c5f7d082d17d3489f2e83", "Expected r1 UI"
    assert sha(memory) == "213956eed3e3869254622b44473b635a26932b7321f7ceec185cf1e5d9a75bb8", "Expected r1 Memory"
    output.mkdir(parents=True, exist_ok=False)
    csc = "C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    cecil = REPO / "package/VaMAcceleration/runtime/worker/Mono.Cecil.dll"
    shutil.copyfile(cecil, output / "Mono.Cecil.dll")

    def run(argv: list[str]) -> None:
        subprocess.run(argv, cwd=output, check=True, timeout=120)

    for name in ("PatchDesktop", "PatchCompiler"):
        run([csc, "/nologo", "/reference:" + str(cecil), "/out:" + name + ".exe", str(SOURCE / (name + ".cs"))])
    run([csc, "/nologo", "/reference:" + str(cecil), "/main:CompilerContract", "/out:CompilerContract.exe",
         str(SOURCE / "CompilerContract.cs"), str(SOURCE / "PatchDesktop.cs")])
    refs = [game / "VaM_Data/Managed" / (n + ".dll") for n in (
        "mscorlib", "Assembly-CSharp", "UnityEngine", "UnityEngine.CoreModule",
        "UnityEngine.UIModule", "UnityEngine.InputModule")]
    run([csc, "/nologo", "/noconfig", "/nostdlib+", "/optimize+", "/target:library", "/out:DesktopHelper.dll",
         *["/reference:" + str(p) for p in refs], str(SOURCE / "DesktopBrowserLayout.cs")])
    desktop = str(output / "PatchDesktop.exe")
    compiler = str(output / "PatchCompiler.exe")
    new_ui = str(output / "Quest3TriggerUI.payload.dll.disabled")
    new_memory = str(output / "VaM.Memory.dll")
    worker = str(output / "VaM.CompilerWorker.exe")
    runner = str(SOURCE / "worker_runner.py")
    run([desktop, "patch", str(ui), "DesktopHelper.dll", new_ui])
    run([desktop, "verify", str(ui), new_ui, "DesktopHelper.dll"])
    run([compiler, "extract", str(memory), "BASELINE_WORKER.exe", "BASELINE_RUNNER.py"])
    run([compiler, "worker", "BASELINE_WORKER.exe", worker])
    run([compiler, "resources", str(memory), worker, runner, new_memory])
    run([str(output / "CompilerContract.exe"), str(memory), new_memory, "BASELINE_WORKER.exe", worker])
    run([csc, "/nologo", "/out:LayoutTests.exe", str(SOURCE / "LayoutTests.cs"), str(SOURCE / "DesktopBrowserLayout.cs")])
    run([str(output / "LayoutTests.exe"), "modified"])
    print("BUILD_REVISION_PASS uiSHA256=" + sha(Path(new_ui)) + " memorySHA256=" + sha(Path(new_memory)))


if __name__ == "__main__":
    main()
