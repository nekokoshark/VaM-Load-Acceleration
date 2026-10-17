"""Build the dependency-first bootstrap and freeze its private DLL set."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--compiler', type=Path, default=Path('C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'))
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    app = repo / 'package/VaMAcceleration'
    output = app / 'tools/WorkerBootstrap.dll'
    subprocess.run([str(args.compiler), '/noconfig', '/nologo', '/nostdlib+', '/optimize+', '/target:library',
                    '/out:' + str(output), '/reference:' + str(args.game / 'VaM_Data/Managed/mscorlib.dll'),
                    '/reference:' + str(args.game / 'VaM_Data/Managed/System.dll'),
                    str(repo / 'src/offline/WorkerBootstrap.cs')], check=True)
    private = app / 'runtime/worker'
    private.mkdir(parents=True, exist_ok=True)
    for source in (app / 'payload/BepInEx/core').glob('*.dll'):
        shutil.copyfile(source, private / source.name)
    files = [*private.glob('*.dll'), *[p for p in (app / 'tools').iterdir() if p.suffix in ('.dll', '.exe')]]
    manifest_path = app / 'components.json'
    manifest = json.loads(manifest_path.read_text('utf-8'))
    manifest['installerVersion'] = '1.8.26-r1'
    manifest['worker'] = {p.relative_to(app).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(files)}
    manifest_path.write_text(json.dumps(manifest, indent=2) + '\n', 'utf-8')
    print('BUILD_WORKER_PASS pinned=' + str(len(list(private.glob('*.dll')))) + ' helpersFrozen=True runtimeVersion=1.8.26')


if __name__ == '__main__':
    main()
