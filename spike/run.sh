#!/usr/bin/env bash
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH"
cd "$(dirname "$0")"
exec dotnet run --project Spike -- "$@"
