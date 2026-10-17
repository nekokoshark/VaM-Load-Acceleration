"""End-to-end offline release test; uses disposable copies, never starts VaM."""
from __future__ import annotations
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import zipfile

HERE = Path(__file__).resolve().parent
GAME = HERE.parent.parent
BASELINES = GAME
SCRIPT_ROOT = HERE
APP = HERE.parent / 'package/VaMAcceleration'
ROOT = HERE / 'verification 中文 space'
RECORDS: list[dict] = []

def digest(p: Path) -> str:
    with p.open('rb') as f: return hashlib.file_digest(f, 'sha256').hexdigest()

def run(label: str, argv: list[str], data: str = '', code: int = 0, cwd: Path = HERE) -> str:
    r = subprocess.run(argv, cwd=cwd, input=data.encode(), capture_output=True, timeout=600)
    row = dict(label=label, argv=argv, cwd=str(cwd), input=data, stdout=r.stdout.decode('utf-8', 'replace'),
               stderr=r.stderr.decode('utf-8', 'replace'), exit=r.returncode)
    RECORDS.append(row)
    print(label, 'exit=' + str(r.returncode), row['stdout'][-3500:], flush=True)
    (HERE / 'TEST_RECORDS.json').write_text(json.dumps(RECORDS, ensure_ascii=False, indent=2) + '\n', 'utf-8')
    assert r.returncode == code, row
    return row['stdout']

def main() -> None:
    assert not ROOT.exists(), 'Use a new disposable verification directory'
    ROOT.mkdir()
    for directory in ('VaM_Data/Managed', 'Mono', 'BepInEx/core'):
        shutil.copytree(GAME / directory, ROOT / directory)
    shutil.copytree(APP, ROOT / 'VaMAcceleration')
    shutil.copyfile(BASELINES / 'Assembly-CSharp.dll', ROOT / 'VaM_Data/Managed/Assembly-CSharp.dll')
    shutil.copyfile(BASELINES / 'mono.dll', ROOT / 'Mono/EmbedRuntime/mono.dll')
    skin = ROOT / 'PerformancePatches/SkinMeshPartDLL.dll'; skin.parent.mkdir()
    shutil.copyfile(BASELINES / 'SkinMeshPartDLL.dll', skin)
    (ROOT / 'VaM.exe').touch()
    (ROOT / 'prefs.json').write_text('{"cacheFolder":"cache 中文 space"}', 'utf-8')
    packages = ROOT / 'AddonPackages'; packages.mkdir()
    with zipfile.ZipFile(packages / 'Test.Morphs.1.var', 'w', zipfile.ZIP_DEFLATED) as z:
        for sex in ('female', 'male'):
            z.writestr('Custom/Atom/Person/Morphs/' + sex + '/test.vmi', json.dumps(dict(id='test', displayName='test', group='Actor', region='Body', min=-1, max=1, numDeltas=1, formulas=[]), ensure_ascii=False))
            z.writestr('Custom/Atom/Person/Morphs/' + sex + '/test.vmb', struct.pack('<iifff', 1, 265, 0.1, 0, 0))
    with zipfile.ZipFile(packages / 'everlaster.Naturalis.55.var', 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('Custom/Scripts/everlaster/Naturalis/src/Common/MorphListener.cs', (BASELINES / 'MorphListener55.cs').read_bytes())
        z.writestr('meta.json', '{"licenseType":"CC BY"}')
        z.writestr('unchanged.bin', b'Unchanged fixture asset\x00\x01')
    cache = ROOT / 'cache 中文 space'; cache.mkdir()
    for name, pixel in [('opaque_C', b'\x30\x60\x90\xff'), ('transparent_C', b'\x30\x60\x90\x50'), ('normal_N', b'\x80\x80\xff\xff')]:
        data = cache / (name + '.vamcache'); data.write_bytes(pixel * (256 * 256))
        Path(str(data) + 'meta').write_text(json.dumps(dict(width='256', height='256', format='RGBA32', size=str(data.stat().st_size), date='fixture')), 'utf-8')
    before = {p.relative_to(ROOT).as_posix(): digest(p) for p in ROOT.rglob('*') if p.is_file() and not p.is_relative_to(ROOT / 'VaMAcceleration')}
    (HERE / 'FIXTURE_BASELINE.json').write_text(json.dumps(before, ensure_ascii=False, indent=2), 'utf-8')
    runner = SCRIPT_ROOT / 'run_fixture.py'
    cli = [str(ROOT / 'VaMAcceleration/runtime/python/python.exe'), str(runner), str(ROOT / 'VaMAcceleration/tools/installer.py')]
    run('BASELINE', [*cli, 'check'])
    assert not (cache / 'VaMMemory').exists()
    modified = run('MODIFIED', [*cli, 'install'])
    assert '"converted": 1' in modified and '"naturalisPatched": 1' in modified
    assert '"ClassificationReceipts": 4' in modified and 'OFFLINE_BUILD_RESULT packages=1' in modified
    assert json.loads((cache / 'opaque_C.vamcachemeta').read_text())['format'] == 'BC7'
    assert digest(cache / 'transparent_C.vamcache') == before['cache 中文 space/transparent_C.vamcache']
    assert digest(cache / 'normal_N.vamcache') == before['cache 中文 space/normal_N.vamcache']
    with zipfile.ZipFile(packages / 'everlaster.Naturalis.55.var') as z:
        assert z.read('unchanged.bin') == b'Unchanged fixture asset\x00\x01'
        profile = next(r for r in json.loads((APP / 'naturalis.json').read_text())['versions'] if r['name'] == 'everlaster.Naturalis.55.var')
        assert hashlib.sha256(z.read(profile['entry'])).hexdigest() == profile['target']
    # Keep the modified test target changed; rollback is tested on an independent second copy.
    rollback = HERE / 'rollback 中文 copy'; shutil.copytree(ROOT, rollback)
    for p in (rollback / 'VaMAcceleration/backups').glob('*/state.json'):
        state = json.loads(p.read_text()); state['game'] = str(rollback); state['cache'] = str(rollback / 'cache 中文 space')
        for row in state['entries']: row['path'] = str(rollback / Path(row['path']).relative_to(ROOT))
        p.write_text(json.dumps(state, ensure_ascii=False, indent=2), 'utf-8')
    rbcli = [str(rollback / 'VaMAcceleration/runtime/python/python.exe'), str(runner), str(rollback / 'VaMAcceleration/tools/installer.py')]
    run('ROLLBACK', [*rbcli, 'restore'])
    for name, expected in before.items(): assert digest(rollback / name) == expected, name
    assert not any((rollback / 'cache 中文 space/VaMMemory').rglob('*.mri'))
    assert digest(ROOT / 'VaM_Data/Managed/Assembly-CSharp.dll') == json.loads((APP / 'components.json').read_text())['patches']['VaM_Data/Managed/Assembly-CSharp.dll'][0]['target']
    run('ROLLBACK_BASELINE', [*rbcli, 'check'])
    # A repeat installation retains the JSON source timestamps and produces no rewrites.
    first = {str(p): (digest(p), p.stat().st_mtime_ns) for p in (cache / 'PackageJSON').glob('*')}
    repeat = run('IDEMPOTENT', [*cli, 'install'])
    assert '"converted": 0' in repeat
    assert first == {str(p): (digest(p), p.stat().st_mtime_ns) for p in (cache / 'PackageJSON').glob('*')}
    newest = sorted((ROOT / 'VaMAcceleration/backups').glob('*/state.json'))[-1]
    assert json.loads(newest.read_text())['entries'] == [], 'Repeat installation rewrote stable outputs'
    # Typed preflight rejects unknown binary before any production-like changes.
    p = rollback / 'VaM_Data/Managed/Assembly-CSharp.dll'; original = p.read_bytes(); p.write_bytes(b'unsupported core')
    bad = run('UNSUPPORTED', [*rbcli, 'install'], code=70)
    assert 'UNSUPPORTED_BASE' in bad; p.write_bytes(original)
    # Inject a corrupt classification fingerprint after core replacement: full failure rollback.
    profiles = rollback / 'VaMAcceleration/tools/profiles.json'; good = profiles.read_bytes(); j = json.loads(good); j[0]['fingerprint'] = '0' * 64; profiles.write_text(json.dumps(j))
    failure = run('FAILURE_ROLLBACK', [*rbcli, 'install', '--no-bc7'], code=70)
    assert 'FAILURE_ROLLBACK files=' in failure
    for name, expected in before.items(): assert digest(rollback / name) == expected, name
    profiles.write_bytes(good)
    print('RELEASE_TEST_PASS baseline=check modified=full-install rollback=independent-copy BC7=real-encode-decode idempotent=True failureRollback=True productionWrites=0')

if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--baselines', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    args = parser.parse_args()
    GAME, BASELINES, HERE = args.game.resolve(), args.baselines.resolve(), args.work.resolve()
    HERE.mkdir(parents=True, exist_ok=True)
    ROOT = HERE / 'verification 中文 space'
    try: main()
    except Exception as e:
        print('TOOL_EXCEPTION:', repr(e)); raise
