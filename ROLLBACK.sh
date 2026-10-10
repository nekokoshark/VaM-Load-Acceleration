#!/usr/bin/env bash
set -euo pipefail

target="${1:?Pass the independently installed VaM root to restore}"
target_unix="$(cygpath -u -- "$target")"
target_windows="$(cygpath -aw -- "$target")"
test -f "$target_unix/VaM.exe"
test -f "$target_unix/VaMAcceleration/tools/installer.py"
exec "$target_unix/VaMAcceleration/runtime/python/python.exe" -u \
    "$target_windows\VaMAcceleration\tools\installer.py" restore --root "$target_windows"
