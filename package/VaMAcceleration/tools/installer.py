"""Offline installer for the frozen VaM 1.8.26 acceleration set (Windows x64)."""
from __future__ import annotations
import argparse
import csv
import ctypes
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
import zlib

APP = Path(__file__).resolve().parent.parent
SUBDIRS = ('MorphImports', 'ClassificationReceipts', 'PackageTrees', 'BundleDiskCache', 'CompilerBytecode', 'StartupMorphIndex')

def sha(p: Path) -> str | None:
    if not p.is_file(): return None
    with p.open('rb') as f: return hashlib.file_digest(f, 'sha256').hexdigest()

def json_read(p: Path) -> dict:
    return json.loads(p.read_text('utf-8-sig'))

def atomic(p: Path, data: bytes) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix='.vamacc-', dir=p.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            stream.write(data); stream.flush(); os.fsync(stream.fileno())
        os.replace(name, p)
    finally:
        if os.path.exists(name): os.unlink(name)

def game_stopped() -> None:
    # Global check: deliberately stops also when another VaM installation is running.
    r = subprocess.run(['tasklist.exe', '/FO', 'CSV', '/NH'], capture_output=True, check=True)
    if any(row and row[0].casefold() == 'vam.exe' for row in csv.reader(io.StringIO(r.stdout.decode('mbcs')))):
        raise RuntimeError('GAME_RUNNING: exit VaM normally before running this tool')

def cache_root(game: Path) -> Path:
    prefs = json_read(game / 'prefs.json') if (game / 'prefs.json').is_file() else {}
    value = prefs.get('cacheFolder') or 'Cache'
    if not isinstance(value, str): raise ValueError('cacheFolder must be a string')
    p = Path(value)
    return Path(os.path.abspath(p if p.is_absolute() else game / p))

def catalog(root: Path) -> list[Path]:
    result: list[Path] = []
    pending = [(root, ())]
    while pending:
        directory, parents = pending.pop()
        if not directory.exists(): continue
        key = str(directory.resolve()).casefold()
        if key in parents:
            print('SKIP_DIRECTORY_CYCLE', directory); continue
        with os.scandir(directory) as children:
            for child in children:
                if child.is_dir(follow_symlinks=True): pending.append((Path(child.path), (*parents, key)))
                elif child.is_file(follow_symlinks=True) and child.name.casefold().endswith('.var'):
                    result.append(Path(os.path.abspath(child.path)))
    by_name: dict[str, list[Path]] = {}
    for p in result: by_name.setdefault(p.stem.casefold(), []).append(p)
    valid = []
    for paths in by_name.values():
        if len(paths) > 1: print('SKIP_AMBIGUOUS_UID', paths[0].stem, 'count=' + str(len(paths)))
        else:
            p = paths[0]
            if any(c in str(p) for c in '\r\n|'): print('SKIP_PROTOCOL_PATH', p)
            elif not re.fullmatch(r'.+\..+\.\d+', p.stem): print('SKIP_NONSTANDARD_UID', p.name)
            else: valid.append(p)
    return sorted(valid, key=lambda p: str(p).casefold())

class Transaction:
    def __init__(self, game: Path, cache: Path, folder: Path):
        self.game, self.cache, self.folder = game, cache, folder
        self.state_path = folder / 'state.json'
        self.state = dict(status='installing', game=str(game), cache=str(cache), entries=[])
        folder.mkdir(parents=True)
        self.save()

    def save(self) -> None:
        atomic(self.state_path, (json.dumps(self.state, ensure_ascii=False, indent=2) + '\n').encode())

    def allowed(self, p: Path) -> None:
        # Lexical VAR links are replaced, never written through to shared backing libraries.
        p = Path(os.path.abspath(p))
        if not any(p.is_relative_to(root) for root in (self.game, self.cache)):
            raise ValueError('Transaction target escaped game/cache: ' + str(p))

    def put(self, p: Path, source: Path | bytes) -> bool:
        return self.put_pair([(p, source)])

    def put_pair(self, items: list[tuple[Path, Path | bytes]]) -> bool:
        prepared = []
        for p, source in items:
            self.allowed(p)
            digest = sha(source) if isinstance(source, Path) else hashlib.sha256(source).hexdigest()
            before = sha(p)
            if before == digest: continue
            if p.exists() and not p.is_file(): raise ValueError('Not a regular file: ' + str(p))
            prepared.append((p, source, before, digest))
        if not prepared: return False
        game_stopped()
        start = len(self.state['entries'])
        for p, source, before, digest in prepared:
            index = len(self.state['entries'])
            stat = p.stat() if before else None
            link = os.readlink(p) if p.is_symlink() else None
            backup = self.folder / 'backup' / str(index)
            backup.parent.mkdir(exist_ok=True)
            if before and not link:
                backup.parent.mkdir(exist_ok=True); shutil.copy2(p, backup)
                if sha(backup) != before: raise RuntimeError('Backup verification failed')
            entry = dict(path=str(p), before=before, after=digest, link=link,
                         atime=stat.st_atime_ns if stat else None, mtime=stat.st_mtime_ns if stat else None,
                         backup=str(backup.relative_to(self.folder)), status='pending')
            self.state['entries'].append(entry)
        self.save()  # Durable write-ahead record for the entire pair.
        for offset, (p, source, before, digest) in enumerate(prepared):
            game_stopped()
            if sha(p) != before: raise RuntimeError('Target changed before commit: ' + str(p))
            row = self.state['entries'][start + offset]
            if row['link']:
                # Move the link itself, so restoring does not require symlink-creation privileges.
                os.replace(p, self.folder / row['backup'])
            if isinstance(source, Path):
                fd, name = tempfile.mkstemp(prefix='.vamacc-', dir=p.parent if p.parent.exists() else self._parent(p))
                os.close(fd)
                try:
                    shutil.copyfile(source, name)
                    with open(name, 'rb+') as f: os.fsync(f.fileno())
                    if sha(Path(name)) != digest: raise RuntimeError('Stage hash mismatch')
                    os.replace(name, p)
                finally:
                    if os.path.exists(name): os.unlink(name)
            else: atomic(p, source)
            if sha(p) != digest: raise RuntimeError('Commit hash mismatch')
            self.state['entries'][start + offset]['status'] = 'written'
            self.save()
        return True

    @staticmethod
    def _parent(p: Path) -> Path:
        p.parent.mkdir(parents=True, exist_ok=True)
        return p.parent

    @staticmethod
    def derived_cache(p: Path, cache: Path) -> bool:
        # Only formats this installer generates/converts, in their recorded cache root.
        if not p.is_relative_to(cache): return False
        relative = p.relative_to(cache)
        patterns = {
            'MorphImports': r'imp-[0-9A-Fa-f]{64}\.mri',
            'ClassificationReceipts': r'[0-9A-Fa-f]{64}\.mcr',
            'PackageTrees': r'[0-9A-Fa-f]{64}\.vjt',
        }
        if len(relative.parts) == 3 and relative.parts[0] == 'VaMMemory':
            pattern = patterns.get(relative.parts[1])
            return bool(pattern and re.fullmatch(pattern, p.name))
        if p.parent == cache / 'PackageJSON':
            return bool(re.fullmatch(r'.+\..+\.\d+\.vamcache(?:json|meta)', p.name))
        return ('VaMMemory' not in relative.parts
            and (p.name.endswith('_C.vamcache') or p.name.endswith('_C.vamcachemeta')))

    @classmethod
    def restore(cls, folder: Path, game: Path) -> int:
        state = json_read(folder / 'state.json')
        if Path(state['game']) != game: raise ValueError('Backup belongs to a different game root')
        cache = Path(state['cache'])
        count = 0
        for index in range(len(state['entries']) - 1, -1, -1):
            row = state['entries'][index]
            if row['status'] == 'restored': continue
            p = Path(row['path'])
            if not any(p.is_relative_to(root) for root in (game, cache)): raise ValueError('Invalid backup target')
            current = sha(p)
            backup = folder / row['backup']
            if row['link'] and backup.is_symlink():
                target = Path(row['link'])
                if not target.is_absolute(): target = p.parent / target
                if sha(target) != row['before']: raise RuntimeError('RESTORE_LINK_SOURCE_CONFLICT: ' + str(p))
                if current not in (None, row['before'], row['after']): raise RuntimeError('RESTORE_CONFLICT: later modification at ' + str(p))
                game_stopped(); os.replace(backup, p)
                if sha(p) != row['before'] or os.readlink(p) != row['link']: raise RuntimeError('Link restore mismatch')
                row['status'] = 'restored'; count += 1
                atomic(folder / 'state.json', (json.dumps(state, ensure_ascii=False, indent=2) + '\n').encode())
                continue
            if current == row['before'] and (not row['link'] or p.is_symlink()):
                row['status'] = 'restored'; continue
            if current != row['after']:
                if (row['link'] or p.is_symlink() or (p.exists() and not p.is_file())
                    or not cls.derived_cache(p, cache)):
                    raise RuntimeError('RESTORE_CONFLICT: later modification at ' + str(p))
                # These derived files change during normal use. Save that version
                # before restoring the recorded pre-install bytes, including pairs.
                if current is not None:
                    game_stopped()
                    saved = folder / 'preserved-cache' / (str(index) + '-' + current)
                    saved.parent.mkdir(exist_ok=True)
                    if not saved.exists():
                        fd, name = tempfile.mkstemp(prefix='.cache-', dir=saved.parent)
                        os.close(fd)
                        try:
                            shutil.copy2(p, name)
                            if sha(Path(name)) != current: raise RuntimeError('Cache preservation hash mismatch')
                            os.replace(name, saved)
                        finally:
                            if os.path.exists(name): os.unlink(name)
                    if sha(saved) != current: raise RuntimeError('Cache preservation hash mismatch')
                    if sha(p) != current: raise RuntimeError('Cache changed during preservation: ' + str(p))
                    row['cacheConflictBackup'] = str(saved.relative_to(folder))
                    row['cacheConflictSHA256'] = current
                    print('RESTORE_CACHE_SAVED', p, 'backup=' + str(saved))
                else:
                    print('RESTORE_CACHE_MISSING', p)
                row['restoreDisposition'] = 'derived-cache-restore'
                atomic(folder / 'state.json', (json.dumps(state, ensure_ascii=False, indent=2) + '\n').encode())
            game_stopped()
            if row['before'] is None: p.unlink()
            elif row['link']:
                raise RuntimeError('Original symbolic link backup is missing')
            else:
                b = folder / row['backup']
                if sha(b) != row['before']: raise RuntimeError('Backup hash mismatch')
                atomic(p, b.read_bytes())
                os.utime(p, ns=(row['atime'], row['mtime']))
            if sha(p) != row['before']: raise RuntimeError('Restore hash mismatch')
            row['status'] = 'restored'; count += 1
            atomic(folder / 'state.json', (json.dumps(state, ensure_ascii=False, indent=2) + '\n').encode())
        state['status'] = 'restored'
        atomic(folder / 'state.json', (json.dumps(state, ensure_ascii=False, indent=2) + '\n').encode())
        return count

def patched(before: bytes, row: dict) -> bytes:
    if hashlib.sha256(before).hexdigest() != row['base']: raise ValueError('Patch base mismatch')
    delta = zlib.decompress((APP / row['patch']).read_bytes())
    if len(delta) != row['length']: raise ValueError('Patch length mismatch')
    result = bytes(v ^ (before[i] if i < len(before) else 0) for i, v in enumerate(delta))
    if hashlib.sha256(result).hexdigest() != row['target']: raise ValueError('Patch result mismatch')
    return result

def configuration(p: Path, updates: dict[tuple[str, str], str]) -> bytes:
    text = p.read_text('utf-8-sig') if p.exists() else ''
    lines = text.splitlines(); section = ''; todo = dict(updates); output = []
    seen: set[tuple[str, str]] = set()
    for line in lines:
        match = re.fullmatch(r'\s*\[([^\]]+)\]\s*', line)
        if match: section = match[1]
        elif '=' in line and not line.lstrip().startswith(('#', ';')):
            key = line.split('=', 1)[0].strip(); pair = (section, key)
            if pair in updates:
                if pair in seen: raise ValueError('Duplicate owned configuration key: ' + key)
                line = key + ' = ' + updates[pair]; todo.pop(pair, None); seen.add(pair)
        output.append(line)
    for section in dict.fromkeys(k[0] for k in todo):
        output += ['', '[' + section + ']']
        output += [k[1] + ' = ' + value for k, value in todo.items() if k[0] == section]
    return ('\n'.join(output).strip() + '\n').encode('utf-8')

def preflight(game: Path) -> list[tuple[Path, Path | bytes]]:
    if not (game / 'VaM.exe').is_file(): raise ValueError('VaM.exe missing; extract ZIP into the VaM root')
    manifest = json_read(APP / 'components.json')
    plans: list[tuple[Path, Path | bytes]] = []
    for name, rows in manifest['patches'].items():
        p = game / name; current = sha(p)
        if current == rows[0]['target']: continue
        row = next((r for r in rows if r['base'] == current), None)
        if row is None: raise ValueError('UNSUPPORTED_BASE: ' + name + ' SHA256=' + str(current))
        plans.append((p, patched(p.read_bytes(), row)))
    for name, digest in manifest['payload'].items():
        source = APP / 'payload' / name
        if sha(source) != digest: raise ValueError('Damaged payload: ' + name)
        p = game / name
        if (name.startswith('BepInEx/core/') or name == 'winhttp.dll') and p.exists() and sha(p) != digest:
            raise ValueError('BOOTSTRAP_CONFLICT: ' + name)
        plans.append((p, source))
    for name in ('Quest3TriggerUI.dll', 'Quest3TriggerUI.payload.dll'):
        if list((game / 'BepInEx/plugins').rglob(name)): raise ValueError('DUPLICATE_Q3_PLUGIN: ' + name)
    door = game / 'doorstop_config.ini'
    if door.exists() and not re.search(r'targetAssembly\s*=\s*BepInEx[\\/]core[\\/]BepInEx.Preloader.dll', door.read_text('utf-8-sig'), re.I):
        raise ValueError('DOORSTOP_TARGET_CONFLICT')
    plans.append((door, configuration(door, {('UnityDoorstop', 'enabled'): 'true',
        ('UnityDoorstop', 'targetAssembly'): r'BepInEx\core\BepInEx.Preloader.dll'})))
    memory = game / 'BepInEx/config/VaM.Memory.cfg'
    plans.append((memory, configuration(memory, {('TextureLoading', 'Bc7ToolPath'): str(game / 'BepInEx/plugins/Quest3TriggerUI/texconv.exe'),
        ('Diagnostics', 'CriticalPathTiming'): 'false'})))
    ini = game / 'PerformancePatches/SkinMeshPartDLL.ini'
    if not ini.exists():
        n = max(1, min(8, (os.cpu_count() or 2) // 2))
        profile = '\n'.join(f'{key}={n}' for key in ('computeColliders', 'skinmeshPart', 'applyMorphs'))
        plans.append((ini, ('[threads]\n' + profile + '\nskinmeshPartMaxPerChar=4\napplyMorphMaxPerChar=4\n'
                           '[threadsVR]\n' + profile + '\nskinmeshPartMaxPerChar=4\napplyMorphMaxPerChar=4\n[profiler]\nenabled=0\n').encode()))
    return plans

def patch_packages(paths: list[Path], tx: Transaction, work: Path) -> int:
    profiles = json_read(APP / 'naturalis.json')['versions']; count = 0
    for p in paths:
        profile = next((r for r in profiles if r['name'].casefold() == p.name.casefold()), None)
        if not profile: continue
        with zipfile.ZipFile(p) as old:
            infos = old.infolist()
            if len([i for i in infos if i.filename == profile['entry']]) != 1:
                print('NATURALIS_SKIP ambiguous entry', p.name); continue
            source = old.read(profile['entry']); digest = hashlib.sha256(source).hexdigest()
            if digest == profile['target']: continue
            if digest != profile['base']:
                print('NATURALIS_SKIP source hash differs', p.name); continue
            changed = patched(source, profile)
            candidate = work / 'candidate.var'
            with zipfile.ZipFile(candidate, 'w') as new:
                new.comment = old.comment
                for info in infos:
                    if info.filename == profile['entry']: new.writestr(info, changed)
                    else:
                        with old.open(info) as src, new.open(info, 'w', force_zip64=info.file_size >= 2**31) as dst:
                            shutil.copyfileobj(src, dst, 1024 * 1024)
            with zipfile.ZipFile(candidate) as verify:
                if verify.testzip() is not None or verify.namelist() != old.namelist(): raise ValueError('VAR rewrite failed')
        tx.put(p, candidate); candidate.unlink(); count += 1
        print('NATURALIS_PATCHED', p.name)
    return count

def prepare_worker(game: Path, work: Path, plans: list[tuple[Path, Path | bytes]]) -> None:
    """Stage frozen dependencies and reconstructed targets without changing the game."""
    work.mkdir()
    manifest = json_read(APP / 'components.json')
    dependencies = []
    for name, digest in manifest['worker'].items():
        source = APP / name
        if sha(source) != digest: raise ValueError('WORKER_DEPENDENCY_DAMAGED: ' + name)
        shutil.copyfile(source, work / source.name)
        if name.startswith('runtime/worker/'):
            dependencies.append(source.name + '|' + digest)
    for name in ('profiles.json',): shutil.copyfile(APP / 'tools' / name, work / name)
    shutil.copyfile(APP / 'payload/BepInEx/plugins/VaMMemory/VaM.Memory.dll', work / 'VaM.Memory.dll')
    shutil.copyfile(work / 'VaM.Memory.dll', work / 'MODIFIED_FILE.dll')
    sources = dict(plans)
    for name, dest in [('VaM_Data/Managed/Assembly-CSharp.dll', 'Assembly-CSharp.dll'),
                       ('Mono/EmbedRuntime/mono.dll', 'mono.dll')]:
        source = sources.get(game / name, game / name)
        if isinstance(source, bytes): (work / dest).write_bytes(source)
        else: shutil.copyfile(source, work / dest)
    settings(work, 'worker-dependencies.txt', dependencies)
    settings(work, 'portable-settings.txt', [str(game)])


def host(game: Path, work: Path, helper: str, methods: list[str]) -> str:
    game_stopped()
    settings(work, 'worker-helper.txt', [helper])
    argv = [str(work / 'native_port_host.exe'), str(work / 'mono.dll'),
        str(game / 'VaM_Data/Managed'), str(game / 'Mono/etc'), 'v2.0.50727', str(work / 'WorkerBootstrap.dll'),
        ';'.join(map(str, [work, game / 'VaM_Data/Managed']))]
    r = subprocess.run(argv, cwd=work, input=('\n'.join([*methods, 'QUIT']) + '\n').encode(), capture_output=True, timeout=7200)
    output = r.stdout.decode('utf-8', 'replace').replace('\r', '')
    with (work / 'workers.log').open('a', encoding='utf-8') as log:
        log.write(json.dumps(dict(argv=argv, input=methods, exit=r.returncode), ensure_ascii=False) + '\n' + output + r.stderr.decode('utf-8', 'replace'))
    if r.returncode or any(v in output for v in ('TOOL_EXCEPTION:', 'OFFLINE_FAILED', 'ACTION_FAILED')):
        raise RuntimeError('WORKER_FAILED: ' + output[-3500:] + r.stderr.decode('utf-8', 'replace')[-3500:])
    for line in output.splitlines():
        if line.startswith(('WORKER_DEPENDENCIES_PASS', 'PORTABLE_INIT_PASS', 'SOURCE_CACHE_PASS', 'OFFLINE_IMPORT_PASS', 'TREE_BUILD_PASS', 'OFFLINE_BUILD_RESULT')): print(line)
    return output

def settings(work: Path, name: str, rows: list[str]) -> None:
    if any('\n' in x or '\r' in x for x in rows): raise ValueError('Worker line separator in path')
    (work / name).write_text('\n'.join(rows) + '\n', 'utf-8')

def prebuild(game: Path, cache: Path, paths: list[Path], tx: Transaction, work: Path) -> dict:
    worker = work / 'worker'
    native = worker / 'native'; native.mkdir()
    settings(worker, 'source-settings.txt', [str(native), *map(str, paths)])
    output = host(game, worker, 'PortableOffline.dll', ['InitializePortable', 'SourceCaches'])
    match = re.search(r'SOURCE_CACHE_PASS built=(\d+) rejected=(\d+)', output)
    if not match: raise RuntimeError('Source cache completion missing')
    valid = [p for p in paths if all((native / (p.stem + ext)).is_file() for ext in ('.vamcachejson', '.vamcachemeta'))]
    for p in valid:
        tx.put_pair([(cache / 'PackageJSON' / (p.stem + ext), native / (p.stem + ext))
                     for ext in ('.vamcachejson', '.vamcachemeta')])
    imports = worker / 'imports'; imports.mkdir()
    for sex in ('female', 'male'):
        relevant = [p for p in valid if any(k.startswith('Custom/Atom/Person/Morphs/' + sex + '/') and k.endswith('.vmi')
                    for k in json_read(native / (p.stem + '.vamcachejson')))]
        settings(worker, 'full-import-settings.txt', ['Custom/Atom/Person/Morphs/' + sex, str(imports),
            *[str(p) + '|' + str(cache / 'PackageJSON' / (p.stem + '.vamcachejson')) + '|False' for p in relevant]])
        host(game, worker, 'PortableOffline.dll', ['InitializePortable', 'Offline', 'Drop'])
    receipts = worker / 'receipts'; receipts.mkdir()
    classifier_names = {x.strip() for x in re.findall(r'^CLASSIFICATION_PACKAGE (.+)$', output, re.M)}
    classifier_paths = [p for p in valid if p.stem in classifier_names]
    for profile in json_read(APP / 'tools/profiles.json'):
        for start in range(0, len(classifier_paths), 32):
            rows = [str(receipts), str(worker / profile['assembly']), profile['method'], profile['fingerprint'],
                    '512', '65536', str(len(profile['regions'])),
                    *[','.join(map(str, r)) for r in profile['regions']], *map(str, classifier_paths[start:start + 32])]
            settings(worker, 'offline-settings.txt', rows)
            host(game, worker, 'OfflineSmallBuilder.dll', ['Run'])
    trees = worker / 'trees'; trees.mkdir()
    settings(worker, 'tree-settings.txt', [str(trees), *[str(cache / 'PackageJSON' / (p.stem + '.vamcachejson')) for p in valid]])
    host(game, worker, 'PortableOffline.dll', ['InitializePortable', 'PortableTrees'])
    built = {}
    for name, source in [('MorphImports', imports), ('ClassificationReceipts', receipts), ('PackageTrees', trees)]:
        files = [p for p in source.rglob('*') if p.is_file()]
        for p in files: tx.put(cache / 'VaMMemory' / name / p.relative_to(source), p)
        built[name] = len(files)
    built.update(nativeJSON=len(valid), rejected=int(match[2]))
    shutil.copyfile(worker / 'workers.log', tx.folder / 'workers.log')
    return built

def command(argv: list[str], allow_keep: bool = False) -> subprocess.CompletedProcess:
    game_stopped()
    result = subprocess.run(argv, capture_output=True, timeout=7200)
    if result.returncode and not (allow_keep and result.returncode == 3):
        raise RuntimeError('CONVERTER_FAILED: ' + subprocess.list2cmdline(argv) + '\n' + result.stdout.decode('mbcs', 'replace')[-2000:])
    return result

def convert_bc7(cache: Path, tx: Transaction, work: Path) -> dict:
    compatibility = str(APP / 'tools/Bc7Compat.exe'); tool = str(APP / 'tools/texconv.exe')
    count = skipped = saved = 0
    for data in cache.rglob('*_C.vamcache'):
        # Never scan backup files, or other named textures/normal/mask caches.
        if 'VaMMemory' in data.relative_to(cache).parts: continue
        meta = Path(str(data) + 'meta')
        if not meta.is_file(): skipped += 1; continue
        j = json_read(meta); fmt = j.get('format'); length = data.stat().st_size
        if fmt not in ('RGB24', 'RGBA32') or not 256 * 1024 <= length <= 256 * 1024 * 1024:
            skipped += 1; continue
        width, height = int(j['width']), int(j['height'])
        if not 1 <= width <= 16384 or not 1 <= height <= 16384: skipped += 1; continue
        bpp = 3 if fmt == 'RGB24' else 4; w, h = width, height; raw = 0; mips = 0
        for n in range(1, 16):
            raw += w * h * bpp
            if raw == length: mips = n; break
            if w == h == 1: break
            w, h = max(1, w // 2), max(1, h // 2)
        if not mips: skipped += 1; continue
        result = command([compatibility, 'source', str(data), str(width), str(height), str(mips), str(bpp)], True)
        if result.returncode == 3: skipped += 1; continue
        result = command([compatibility, 'plan', str(data), str(width), str(height), str(mips), str(length)], True)
        if result.returncode == 3: skipped += 1; continue
        tw, th, tm, expected = map(int, result.stdout.decode().split())
        with tempfile.TemporaryDirectory(prefix='bc7-', dir=work) as temp:
            stage = Path(temp); source = stage / 'input.dds'; out = stage / 'encoded'; verify = stage / 'decoded'
            out.mkdir(); verify.mkdir()
            command([compatibility, 'input', str(data), str(width), str(height), str(mips), str(bpp), str(source)])
            command([tool, '-nologo', '-dx10', '-f', 'BC7_UNORM', '-bcmax', '-aw', '1024', '-w', str(tw), '-h', str(th), '-m', str(tm), '-o', str(out), str(source)])
            encoded = out / 'input.DDS'
            command([tool, '-nologo', '-dx10', '-f', 'R8G8B8A8_UNORM', '-m', str(tm), '-o', str(verify), str(encoded)])
            result = command([compatibility, 'decoded', str(verify / 'input.DDS'), str(tw), str(th), str(tm)], True)
            if result.returncode == 3: skipped += 1; continue
            payload, metadata = stage / 'data.raw', stage / 'metadata.json'
            command([compatibility, 'extract', str(encoded), str(tw), str(th), str(tm), str(payload)])
            command([compatibility, 'metadata', str(meta), str(tw), str(th), str(tm), str(metadata)])
            if payload.stat().st_size != expected or expected >= length: raise ValueError('BC7 saving/length mismatch')
            tx.put_pair([(data, payload), (meta, metadata)]); count += 1; saved += length - expected
            print('BC7_CONVERTED', data.name, 'savedBytes=' + str(length - expected))
    return dict(converted=count, skipped=skipped, savedBytes=saved)

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['install', 'restore', 'check'])
    parser.add_argument('--root', type=Path, default=APP.parent)
    parser.add_argument('--no-bc7', action='store_true')
    args = parser.parse_args(); game = Path(os.path.abspath(args.root))
    game_stopped(); cache = cache_root(game)
    mutex = ctypes.windll.kernel32.CreateMutexW
    mutex.argtypes = [ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p]; mutex.restype = ctypes.c_void_p
    handle = mutex(None, False, 'Local\\VaMAcceleration_' + hashlib.sha256(str(game).casefold().encode()).hexdigest())
    if not handle: raise ctypes.WinError()
    if ctypes.windll.kernel32.GetLastError() == 183:
        ctypes.windll.kernel32.CloseHandle(ctypes.c_void_p(handle)); raise RuntimeError('INSTALLER_ALREADY_RUNNING')
    tx = None
    try:
        backup_root = game / 'VaMAcceleration/backups'
        unfinished = [p for p in backup_root.glob('*/state.json') if json_read(p)['status'] == 'installing']
        if args.action == 'restore':
            pending = [p for p in backup_root.glob('*/state.json') if json_read(p)['status'] != 'restored']
            if not pending: print('RESTORE_PASS files=0'); return 0
            folder = sorted(p.parent for p in pending)[-1]
            count = Transaction.restore(folder, game)
            print('RESTORE_PASS files=' + str(count)); return 0
        if unfinished: raise RuntimeError('INTERRUPTED_TRANSACTION: run Restore_VaM_Acceleration.bat first')
        plans = preflight(game); paths = catalog(game / 'AddonPackages')
        print('TARGET_ROOT', game); print('CACHE_ROOT', cache); print('CATALOG packages=' + str(len(paths)))
        with tempfile.TemporaryDirectory(prefix='stage-', dir=APP) as temp:
            work = Path(temp)
            try:
                print('STAGE worker-preflight package=' + json_read(APP / 'components.json')['installerVersion'])
                prepare_worker(game, work / 'worker', plans)
                output = host(game, work / 'worker', 'PortableOffline.dll', ['CheckDependencies', 'InitializePortable'])
                if 'PORTABLE_INIT_PASS' not in output: raise RuntimeError('Worker initialization completion missing')
                print('WORKER_PREFLIGHT_PASS gameWrites=0')
                if args.action == 'check': print('CHECK_PASS planned=' + str(len(plans)) + ' writes=0'); return 0
                folder = backup_root / (time.strftime('%Y%m%d-%H%M%S') + '-' + str(os.getpid()))
                tx = Transaction(game, cache, folder)
                print('STAGE install-files')
                for p, source in plans: tx.put(p, source)
                for name in SUBDIRS: (cache / 'VaMMemory' / name).mkdir(parents=True, exist_ok=True)
                packages = patch_packages(paths, tx, work)
                print('STAGE prebuild-caches')
                built = prebuild(game, cache, paths, tx, work)
                bc7 = convert_bc7(cache, tx, work) if not args.no_bc7 else dict(converted=0, skipped=0, savedBytes=0)
            finally:
                log = work / 'worker/workers.log'
                if log.is_file():
                    logs = APP / 'logs'; logs.mkdir(exist_ok=True)
                    dest = logs / (time.strftime('%Y%m%d-%H%M%S') + '-' + str(os.getpid()) + '.log')
                    shutil.copyfile(log, dest); print('WORKER_LOG', dest)
        tx.state.update(status='complete', caches=built, bc7=bc7, naturalisPatched=packages); tx.save()
        print('INSTALL_PASS version=1.8.26 ' + json.dumps(dict(caches=built, bc7=bc7, naturalisPatched=packages), sort_keys=True))
        print('BACKUP', folder)
        return 0
    except Exception:
        if tx is not None:
            try: print('FAILURE_ROLLBACK files=' + str(Transaction.restore(tx.folder, game)))
            except Exception as error: print('ROLLBACK_PENDING', error)
        raise
    finally: ctypes.windll.kernel32.CloseHandle(ctypes.c_void_p(handle))

if __name__ == '__main__':
    try: sys.exit(main())
    except Exception as e:
        print('TOOL_EXCEPTION:', str(e)); sys.exit(70)
