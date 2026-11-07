#!/bin/sh
# Put Vetro's stock dock back from native/dock/.vetro-bar-stock/.
set -e
TV_IP="${TV_IP:-192.168.4.32}"
SDB="${SDB:-$HOME/tizen-studio/tools/sdb}"
PORT="${SDB_PORT:-26101}"

if [ ! -x "$SDB" ]; then
  echo "sdb not found at $SDB" >&2
  exit 1
fi

"$SDB" connect "$TV_IP:$PORT" >/dev/null
"$SDB" root on >/dev/null || true

DOCK="$("$SDB" shell "find /opt/usr /home/owner -name tvlauncher-dotnet.dll 2>/dev/null | grep /.vetro-bar-stock/ | head -1" | tr -d '\r')"
if [ -z "$DOCK" ]; then
  echo "no stock backup on the TV (.vetro-bar-stock). nothing to restore." >&2
  exit 1
fi
STOCK="$(dirname "$DOCK")"
DOCKDIR="$(dirname "$STOCK")"
echo "restoring $DOCKDIR from $STOCK"

"$SDB" shell "set -e
d='$DOCKDIR'
s='$STOCK'
cp \"\$s/tvlauncher-dotnet.dll\" \"\$d/tvlauncher-dotnet.dll\"
if [ -f \"\$s/tvlauncher-dotnet.runtimeconfig.json\" ]; then
  cp \"\$s/tvlauncher-dotnet.runtimeconfig.json\" \"\$d/\"
fi
if [ -d \"\$s/res\" ]; then
  rm -rf \"\$d/res\"
  cp -a \"\$s/res\" \"\$d/res\"
fi
systemctl restart vetro-dock 2>/dev/null || true
if [ -f /home/owner/tvlauncher.pid ]; then
  p=\$(tr -d '\\r' < /home/owner/tvlauncher.pid)
  if [ -n \"\$p\" ]; then kill \"\$p\" 2>/dev/null || true; fi
fi
echo restored
"
