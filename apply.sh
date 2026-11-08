#!/bin/sh
# Put this dock on the TV. The first run keeps Vetro's files in
# native/dock/.vetro-bar-stock/; later runs leave that copy alone.
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
sh "$ROOT/build.sh"
exec python3 "$ROOT/tools/overlay.py" apply
