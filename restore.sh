#!/bin/sh
# Put Vetro's dock back from native/dock/.vetro-bar-stock/.
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
exec python3 "$ROOT/tools/overlay.py" restore
