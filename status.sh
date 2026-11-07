#!/bin/sh
# Show whether the overlay is on the TV, and whether a stock backup exists.
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

"$SDB" shell 'set +e
dll=$(find /opt/usr /home/owner -name tvlauncher-dotnet.dll 2>/dev/null | grep -v /.vetro-bar-stock/ | head -1)
echo "dll=$dll"
if [ -n "$dll" ]; then
  d=$(dirname "$dll")
  echo "dir=$d"
  if [ -d "$d/.vetro-bar-stock" ]; then echo "backup=yes"; else echo "backup=no"; fi
  if [ -d "$d/res/weather" ]; then echo "weather=$(ls "$d/res/weather" 2>/dev/null | tr "\n" " ")"; else echo "weather=missing"; fi
  echo "live_md5=$(md5sum "$dll" 2>/dev/null | awk "{print \$1}")"
  if [ -f "$d/.vetro-bar-stock/tvlauncher-dotnet.dll" ]; then
    echo "stock_md5=$(md5sum "$d/.vetro-bar-stock/tvlauncher-dotnet.dll" 2>/dev/null | awk "{print \$1}")"
  fi
fi
echo "unit=$(systemctl is-active vetro-dock 2>/dev/null || echo unknown)"
if [ -f /home/owner/tvlauncher.pid ]; then echo "pid=$(cat /home/owner/tvlauncher.pid)"; fi
if [ -f /home/owner/tvlauncher.log ]; then echo "--- log ---"; tail -n 12 /home/owner/tvlauncher.log; fi
'
