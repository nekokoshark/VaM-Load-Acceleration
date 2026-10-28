"""Reproduce the 5.4.21 payload from a verified r5 application tree."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

UPSTREAM_SHA = '2af69fe0aaf821e95c4cd3e4d53860e667c54648f97dca6f971a5bcd3c22aa34'

def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--baseline-app', type=Path, required=True)
    parser.add_argument('--upstream-zip', type=Path, required=True)
    parser.add_argument('--output-app', type=Path, required=True)
    args = parser.parse_args()
    old, archive, output = (p.resolve() for p in (args.baseline_app, args.upstream_zip, args.output_app))
    if output.exists():
        raise ValueError('Output must be a new directory')
    if sha(archive) != UPSTREAM_SHA:
        raise ValueError('Expected official BepInEx x64 5.4.21 archive')
    manifest = json.loads((old / 'components.json').read_text('utf-8'))
    if manifest['installerVersion'] != '1.8.26-r5':
        raise ValueError('Expected frozen r5 application')
    for name, digest in manifest['payload'].items():
        if sha(old / 'payload' / name) != digest:
            raise ValueError('Modified baseline payload: ' + name)
    for name, digest in manifest['worker'].items():
        if sha(old / name) != digest:
            raise ValueError('Modified baseline worker: ' + name)
    shutil.copytree(old, output, ignore=shutil.ignore_patterns('backups', 'logs', '__pycache__', 'stage-*'))
    legacy = {n: h for n, h in manifest['payload'].items() if n.startswith('BepInEx/core/') or n == 'winhttp.dll'}
    offline = output / 'runtime/offline/VaM.Memory.dll'
    offline.parent.mkdir(exist_ok=True)
    shutil.copyfile(old / 'payload/BepInEx/plugins/VaMMemory/VaM.Memory.dll', offline)
    manifest['worker']['runtime/offline/VaM.Memory.dll'] = sha(offline)
    with tempfile.TemporaryDirectory(prefix='bep5421-', dir=output.parent) as folder:
        work = Path(folder)
        with zipfile.ZipFile(archive) as z:
            for name in legacy:
                destination = output / 'payload' / name
                destination.write_bytes(z.read(name))
                manifest['payload'][name] = sha(destination)
        core = output / 'payload/BepInEx/core'
        shutil.copyfile(core / 'Mono.Cecil.dll', work / 'Mono.Cecil.dll')
        compiler = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        tool = work / 'Retarget5421.exe'
        subprocess.run([compiler, '/nologo', '/r:' + str(work / 'Mono.Cecil.dll'),
                        '/r:System.Core.dll', '/out:' + str(tool),
                        str(Path(__file__).with_name('Retarget5421.cs'))], check=True)
        for name in manifest['payload']:
            if '/plugins/' in name and name.endswith(('.dll', '.dll.disabled')):
                destination = output / 'payload' / name
                subprocess.run([str(tool), str(old / 'payload' / name), str(destination), str(core)], check=True)
                manifest['payload'][name] = sha(destination)
    current = Path(__file__).resolve().parents[1] / 'package/VaMAcceleration'
    shutil.copyfile(current / 'tools/installer.py', output / 'tools/installer.py')
    manifest.update(installerVersion='1.8.26-r7-bep5421', game='1.22.0.13',
                    bootstrapTarget='bepinex-5.4.21', bootstrapProfiles={
                        'legacy-5.1': legacy,
                        'bepinex-5.4.21': {n: manifest['payload'][n] for n in legacy}},
                    upstream={'url': 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.21/BepInEx_x64_5.4.21.0.zip',
                              'sha256': UPSTREAM_SHA})
    (output / 'components.json').write_text(json.dumps(manifest, indent=2) + '\n', 'utf-8')
    print('BUILD_BEP5421_PASS runtime=5.4.21 offline=5.1')

if __name__ == '__main__':
    main()
