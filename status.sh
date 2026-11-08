#!/bin/sh
# Show the live dock, whether the original copy is still there, and the last log lines.
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
exec python3 "$ROOT/tools/overlay.py" status
