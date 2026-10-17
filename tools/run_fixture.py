"""Run file-install tests while a real game is open; never target a real executable."""
from __future__ import annotations

import argparse
import importlib.util
from pathlib import Path
import sys


def main() -> int:
    parser = argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument('installer', type=Path)
    parser.add_argument('action', choices=['install', 'restore', 'check'])
    parser.add_argument('--no-bc7', action='store_true')
    args = parser.parse_args()
    installer_path = args.installer.resolve()
    game = installer_path.parents[2]
    marker = game / 'VaM.exe'
    if not marker.is_file() or marker.stat().st_size != 0:
        raise ValueError('Test runner requires an empty VaM.exe marker, not a game executable')
    spec = importlib.util.spec_from_file_location('fixture_installer', installer_path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    # Only the global process guard is replaced; all file checks, native Mono,
    # hashing, dependency binding, conversion and rollback run unchanged.
    module.game_stopped = lambda: None
    sys.argv = [str(installer_path), args.action, '--root', str(game)]
    if args.no_bc7: sys.argv.append('--no-bc7')
    try:
        return module.main()
    except Exception as error:
        print('TOOL_EXCEPTION:', str(error))
        return 70


if __name__ == '__main__':
    raise SystemExit(main())
