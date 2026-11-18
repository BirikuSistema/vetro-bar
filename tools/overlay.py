#!/usr/bin/env python3
"""Apply or restore this dock through Vetro's agent.

sdb can connect, but root is disabled on this TV, so it cannot write the dock
directory. The agent on port 8099 can, and it keeps working while Host PC IP
stays 127.0.0.1. The first apply copies the dock it finds into
native/dock/.vetro-bar-stock/ and later applies leave that copy alone.
"""

import base64
import io
import json
import os
import sys
import tarfile
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TV = os.environ.get("TV_IP", "").strip()
if not TV:
    sys.exit("Set TV_IP to your TV's IP address first, e.g.  export TV_IP=192.168.1.50")
AGENT = "http://%s:8099" % TV
DOCK_MARKERS = ("/.vetro-bar-stock/", "/.buildResult/")

# res/inputs.tsv is Vetro's own catalog sample. The live lists live in
# /opt/usr/home/owner/share/ and must not be replaced from here.
SKIP_RES = {"inputs.tsv"}


def post(path, payload, timeout=40):
    req = urllib.request.Request(
        AGENT + path,
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            body = json.loads(response.read().decode())
    except urllib.error.URLError as error:
        raise SystemExit("cannot reach Vetro's agent at %s (%s)" % (AGENT, error))
    if path == "/exec" and (not body.get("ok") or body.get("exit_code")):
        raise SystemExit(body.get("stderr") or body.get("output") or "the command on the TV failed")
    return body


def sh(cmd, timeout=40):
    return post("/exec", {"cmd": cmd}, timeout).get("output", "")


def dock_dir():
    """Directory of the dock that is actually running, never the build copy."""
    out = sh(r"""
pid=$(cat /home/owner/tvlauncher.pid 2>/dev/null || true)
if [ -n "$pid" ]; then
  cwd=$(readlink -f /proc/$pid/cwd 2>/dev/null || true)
  if [ -f "$cwd/tvlauncher-dotnet.dll" ]; then
    echo "$cwd"
    exit 0
  fi
fi
find /opt/usr /home/owner -name tvlauncher-dotnet.dll 2>/dev/null | while read -r f; do
  case "$f" in
    */.vetro-bar-stock/*|*/.buildResult/*) ;;
    *) echo "$(dirname "$f")"; exit 0 ;;
  esac
done
""")
    dock = out.strip().splitlines()[-1].strip() if out.strip() else ""
    if not dock or any(mark in dock + "/" for mark in DOCK_MARKERS):
        raise SystemExit("cannot find the running dock. Is Vetro started?")
    return dock


def payload():
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.USTAR_FORMAT) as tar:
        dll = ROOT / "bin/Release/netcoreapp3.1/tvlauncher-dotnet.dll"
        if not dll.is_file():
            raise SystemExit("no esta construida la DLL. ejecuta sh build.sh")
        tar.add(str(dll), arcname="tvlauncher-dotnet.dll")
        res = ROOT / "res"
        for path in sorted(res.rglob("*")):
            if not path.is_file() or path.name in SKIP_RES:
                continue
            tar.add(str(path), arcname="res/" + path.relative_to(res).as_posix())
    return buf.getvalue()


def upload(dock):
    blob = base64.b64encode(payload()).decode()
    sh("rm -f /tmp/vb.b64 /tmp/vb.tar")
    for start in range(0, len(blob), 40000):
        sh("printf '%s' '" + blob[start:start + 40000] + "' >> /tmp/vb.b64")
    sh(
        "set -e; base64 -d /tmp/vb.b64 > /tmp/vb.tar; "
        "tar -xf /tmp/vb.tar -C '%s'; rm -f /tmp/vb.b64 /tmp/vb.tar" % dock,
        timeout=60,
    )


def ensure_backup(dock):
    out = sh(r"""
set -e
d='%s'
if [ -d "$d/.vetro-bar-stock" ]; then
  echo backup-exists
  exit 0
fi
mkdir -p "$d/.vetro-bar-stock"
cp "$d/tvlauncher-dotnet.dll" "$d/.vetro-bar-stock/"
if [ -f "$d/tvlauncher-dotnet.runtimeconfig.json" ]; then
  cp "$d/tvlauncher-dotnet.runtimeconfig.json" "$d/.vetro-bar-stock/"
fi
if [ -d "$d/res" ]; then
  cp -a "$d/res" "$d/.vetro-bar-stock/res"
fi
echo backed-up
""" % dock)
    print(out.strip())


def refresh():
    body = post("/dock", {"action": "refresh"}, timeout=30)
    print("dock pid %s  error %s" % (body.get("pid"), body.get("error")))
    if body.get("error"):
        raise SystemExit(1)


def apply():
    dock = dock_dir()
    print("dock: " + dock)
    ensure_backup(dock)
    upload(dock)
    refresh()
    print("overlay applied")


def restore():
    dock = dock_dir()
    print("restoring " + dock)
    out = sh(r"""
set -e
d='%s'
s="$d/.vetro-bar-stock"
if [ ! -f "$s/tvlauncher-dotnet.dll" ]; then
  echo "no hay copia original en $s" >&2
  exit 1
fi
cp "$s/tvlauncher-dotnet.dll" "$d/tvlauncher-dotnet.dll"
if [ -f "$s/tvlauncher-dotnet.runtimeconfig.json" ]; then
  cp "$s/tvlauncher-dotnet.runtimeconfig.json" "$d/"
fi
if [ -d "$s/res" ]; then
  rm -rf "$d/res"
  cp -a "$s/res" "$d/res"
fi
echo restored
""" % dock)
    print(out.strip())
    refresh()


def status():
    dock = dock_dir()
    print(sh(r"""
d='%s'
echo "dock=$d"
if [ -f "$d/.vetro-bar-stock/tvlauncher-dotnet.dll" ]; then echo backup=yes; else echo backup=no; fi
echo "live=$(md5sum "$d/tvlauncher-dotnet.dll" | awk '{print $1}')"
if [ -f "$d/.vetro-bar-stock/tvlauncher-dotnet.dll" ]; then
  echo "stock=$(md5sum "$d/.vetro-bar-stock/tvlauncher-dotnet.dll" | awk '{print $1}')"
fi
if [ -f /home/owner/tvlauncher.pid ]; then echo "pid=$(cat /home/owner/tvlauncher.pid)"; fi
if [ -f /home/owner/tvlauncher.log ]; then echo '--- log ---'; tail -n 8 /home/owner/tvlauncher.log; fi
""" % dock).rstrip())


def main():
    if len(sys.argv) != 2 or sys.argv[1] not in ("apply", "restore", "status"):
        raise SystemExit("uso: overlay.py apply|restore|status")
    {"apply": apply, "restore": restore, "status": status}[sys.argv[1]]()


if __name__ == "__main__":
    main()
