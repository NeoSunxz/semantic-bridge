#!/usr/bin/env bash
# Set EXTRACT=1 for the documented second attempt (IncludeNativeLibrariesForSelfExtract).
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH"
cd "$(dirname "$0")"
extra=()
[ "${EXTRACT:-0}" = "1" ] && extra+=(-p:IncludeNativeLibrariesForSelfExtract=true)
dotnet publish Spike -r linux-x64 --self-contained -p:PublishSingleFile=true -c Release -o out "${extra[@]}"
ls -l out/Spike
exec ./out/Spike "$@"
