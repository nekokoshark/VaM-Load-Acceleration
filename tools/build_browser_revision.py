"""Build the r3 mouse fix from a hash-pinned frozen r2 UI payload."""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import shutil
import subprocess


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game", type=Path, required=True)
    parser.add_argument("--baseline-ui", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    source = repo / "src/browser_mouse_20261022"
    baseline, output, game = (p.resolve() for p in (args.baseline_ui, args.output, args.game))
    assert hashlib.sha256(baseline.read_bytes()).hexdigest() == "ef80141a4b84a0f05d977c60fb48e6979ee391aa1fccda2110c1a371dc855c14", "Expected r2 UI"
    output.mkdir(parents=True, exist_ok=False)
    cecil = repo / "package/VaMAcceleration/runtime/worker/Mono.Cecil.dll"
    shutil.copyfile(cecil, output / "Mono.Cecil.dll")
    csc = "C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"

    def run(argv: list[str]) -> None:
        subprocess.run(argv, cwd=output, check=True, timeout=120)

    run([csc, "/nologo", "/reference:" + str(cecil), "/out:PatchMouse.exe", str(source / "PatchMouse.cs")])
    run([csc, "/nologo", "/noconfig", "/nostdlib+", "/optimize+", "/target:library", "/out:MouseHelper.dll",
         *["/reference:" + str(game / "VaM_Data/Managed" / (name + ".dll"))
           for name in ("mscorlib", "Assembly-CSharp", "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.UIModule", "UnityEngine.InputModule")],
         str(source / "BrowserMouseInput.cs")])
    target = output / "Quest3TriggerUI.payload.dll.disabled"
    run([str(output / "PatchMouse.exe"), str(baseline), "MouseHelper.dll", str(target)])
    print("BUILD_BROWSER_REVISION_PASS SHA256=" + hashlib.sha256(target.read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
