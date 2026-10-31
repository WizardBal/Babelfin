#!/usr/bin/env bash
# Builds the Babelfin plugin (against Jellyfin 12.0.0) and loads it the way Jellyfin 12.2 does.
# Usage: bash plugin/bindtest/run.sh   (from anywhere). Ends with "BIND TEST PASS" or exits non-zero.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
DOTNET="$(command -v dotnet || true)"
[ -z "$DOTNET" ] && DOTNET="/c/Program Files/dotnet/dotnet.exe"
PLUGIN="$HERE/../src/Jellyfin.Plugin.Babelfin"
"$DOTNET" build "$PLUGIN/Jellyfin.Plugin.Babelfin.csproj" -c Release -nologo -v q
"$DOTNET" run --project "$HERE/app/App.csproj" -c Release -- "$PLUGIN/bin/Release/net10.0"
