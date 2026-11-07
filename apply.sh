#!/bin/sh
# Overlay this dock onto the live Vetro install. The stock DLL and its res/ are
# copied once to native/dock/.vetro-bar-stock/ so restore.sh can put them back.
# Never copies app icons other than settings / samsunghome / livetv (Steam stays).
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
TV_IP="${TV_IP:-192.168.4.32}"
SDB="${SDB:-$HOME/tizen-studio/tools/sdb}"
PORT="${SDB_PORT:-26101}"

if [ ! -x "$SDB" ]; then
  echo "sdb not found at $SDB" >&2
  exit 1
fi

sh "$ROOT/build.sh"
DLL="$ROOT/bin/Release/netcoreapp3.1/tvlauncher-dotnet.dll"
if [ ! -f "$DLL" ]; then
  echo "build did not produce $DLL" >&2
  exit 1
fi

echo "connecting $TV_IP:$PORT"
"$SDB" connect "$TV_IP:$PORT" >/dev/null
"$SDB" root on >/dev/null || true

DOCK="$("$SDB" shell "find /opt/usr /home/owner -name tvlauncher-dotnet.dll 2>/dev/null | grep -v /.vetro-bar-stock/ | head -1" | tr -d '\r')"
if [ -z "$DOCK" ]; then
  echo "no Vetro dock DLL on the TV" >&2
  exit 1
fi
DOCKDIR="$(dirname "$DOCK")"
echo "dock: $DOCKDIR"

"$SDB" shell "set -e
d='$DOCKDIR'
if [ ! -d \"\$d/.vetro-bar-stock\" ]; then
  mkdir -p \"\$d/.vetro-bar-stock\"
  cp \"\$d/tvlauncher-dotnet.dll\" \"\$d/.vetro-bar-stock/\"
  if [ -f \"\$d/tvlauncher-dotnet.runtimeconfig.json\" ]; then
    cp \"\$d/tvlauncher-dotnet.runtimeconfig.json\" \"\$d/.vetro-bar-stock/\"
  fi
  if [ -d \"\$d/res\" ]; then cp -a \"\$d/res\" \"\$d/.vetro-bar-stock/res\"; fi
  echo backed-up
else
  echo backup-exists
fi
mkdir -p \"\$d/res/weather\" \"\$d/res/icons\"
"

"$SDB" push "$DLL" "$DOCKDIR/tvlauncher-dotnet.dll"
for icon in settings samsunghome livetv; do
  [ -f "$ROOT/res/icons/$icon.png" ] && "$SDB" push "$ROOT/res/icons/$icon.png" "$DOCKDIR/res/icons/$icon.png"
done
if [ -d "$ROOT/res/weather" ]; then
  for f in "$ROOT/res/weather"/*.png; do
    [ -f "$f" ] && "$SDB" push "$f" "$DOCKDIR/res/weather/$(basename "$f")"
  done
fi

"$SDB" shell "systemctl restart vetro-dock 2>/dev/null || true
if [ -f /home/owner/tvlauncher.pid ]; then
  p=\$(tr -d '\\r' < /home/owner/tvlauncher.pid)
  if [ -n \"\$p\" ]; then kill \"\$p\" 2>/dev/null || true; fi
fi
echo overlay-applied
"
echo "done. restore with: sh $ROOT/restore.sh"
