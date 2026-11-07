#!/usr/bin/env bash
# Smoke test for a published sbridge binary: --version, then a stdio MCP handshake (expects 2 JSON lines).
# Usage: scripts/smoke-mcp.sh <path-to-sbridge>
set -euo pipefail
bin="${1:?usage: smoke-mcp.sh <path-to-sbridge>}"

"$bin" --version

# Fake credentials: the handshake never contacts any service.
export SBRIDGE_TENANT_ID=00000000-0000-0000-0000-000000000000
export SBRIDGE_CLIENT_ID=00000000-0000-0000-0000-000000000000
export SBRIDGE_CLIENT_SECRET=smoke-test-not-a-secret

out="$(mktemp)"
trap 'rm -f "$out"' EXIT
{
  echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}'
  echo '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  echo '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
  sleep 3
} | "$bin" mcp > "$out"

lines="$(grep -c '^{' "$out" || true)"
if [ "$lines" -ne 2 ]; then
  echo "smoke-mcp: expected 2 JSON lines, got $lines" >&2
  exit 1
fi
grep -q '"tools"' "$out" || { echo "smoke-mcp: tools/list response missing tools" >&2; exit 1; }
echo "smoke-mcp: OK"
