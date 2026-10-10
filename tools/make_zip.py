"""Package the explicit distributable tree and reopen every entry for validation."""
from __future__ import annotations
import argparse
import hashlib
from pathlib import Path
import zipfile

def main() -> None:
    ap = argparse.ArgumentParser(); ap.add_argument('--output', type=Path, required=True); args = ap.parse_args()
    root = Path(__file__).resolve().parent.parent / 'package'
    files = sorted(p for p in root.rglob('*') if p.is_file() and '__pycache__' not in p.parts and 'backups' not in p.relative_to(root).parts and p.suffix != '.pyc')
    for p in files:
        if p.name == 'prefs.json' or p.suffix.lower() == '.var': raise ValueError('Private data in distributable tree')
    hashes = {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in files if p.name != 'SHA256SUMS.txt'}
    (root / 'SHA256SUMS.txt').write_text('\n'.join(value + '  ' + key for key, value in hashes.items()) + '\n', 'utf-8')
    files = sorted(p for p in root.rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.suffix != '.pyc' and 'backups' not in p.relative_to(root).parts)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.output, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for p in files:
            info = zipfile.ZipInfo(p.relative_to(root).as_posix(), date_time=(2026, 10, 11, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED; z.writestr(info, p.read_bytes())
    with zipfile.ZipFile(args.output) as z:
        assert z.testzip() is None
        assert set(z.namelist()) == {p.relative_to(root).as_posix() for p in files}
        for key, value in hashes.items(): assert hashlib.sha256(z.read(key)).hexdigest() == value, key
    print('ZIP_VERIFY_PASS files=' + str(len(files)) + ' bytes=' + str(args.output.stat().st_size) + ' SHA256=' + hashlib.sha256(args.output.read_bytes()).hexdigest())

if __name__ == '__main__':
    try: main()
    except Exception as e:
        print('TOOL_EXCEPTION:', str(e)); raise SystemExit(70)
