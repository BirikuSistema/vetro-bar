#!/bin/sh
# Builds the overlay dock (netcoreapp3.1, the runtime on the TV).
set -e
cd "$(dirname "$0")"
dotnet build -c Release Launcher.csproj
echo "built bin/Release/netcoreapp3.1/tvlauncher-dotnet.dll"
