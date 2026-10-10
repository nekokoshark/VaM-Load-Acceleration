"""Recompile the archived Memory sources against a user's local VaM references."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import subprocess

def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', type=Path, required=True)
    ap.add_argument('--output', type=Path, required=True)
    ap.add_argument('--compiler', type=Path, default=Path('C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'))
    args = ap.parse_args()
    root = Path(__file__).resolve().parent.parent / 'src/memory'
    data = json.loads((root / 'build.json').read_text('utf-8'))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    argv = [str(args.compiler), '/noconfig', '/nologo', '/nostdlib+', '/unsafe+', '/optimize+', '/target:library',
            '/out:' + str(args.output.resolve()), *['/reference:' + str(args.game / p) for p in data['references']],
            *['/resource:' + str(root / p['path']) + ',' + p['name'] for p in data['resources']],
            *[str(root / p) for p in data['sources']]]
    rsp = args.output.with_suffix('.rsp')
    rsp.write_text('\n'.join('"' + value + '"' for value in argv[2:]) + '\n', 'utf-8-sig')
    subprocess.run([argv[0], '/noconfig', '@' + str(rsp)], check=True)
    print('BUILD_MEMORY_PASS newMVID=True rebuildMorphTablesBeforeUse=True')

if __name__ == '__main__':
    try: main()
    except Exception as e:
        print('TOOL_EXCEPTION:', str(e)); raise SystemExit(70)
