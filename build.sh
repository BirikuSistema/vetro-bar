#!/bin/sh
# Builds the overlay dock (netcoreapp3.1, the runtime on the TV).
set -e
cd "$(dirname "$0")"
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  PATH="$HOME/.dotnet:$PATH"
  export PATH
fi
dotnet build -c Release Launcher.csproj
echo "built bin/Release/netcoreapp3.1/tvlauncher-dotnet.dll"
