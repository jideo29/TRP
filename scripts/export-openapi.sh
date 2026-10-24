#!/usr/bin/env bash
# Export the fail-closed host OpenAPI document and optionally diff it.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
snapshot="$root/docs/api/wayfarer-openapi-v1.json"
port="${WAYFARER_OPENAPI_PORT:-5088}"
mode="${1:-}"

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://127.0.0.1:${port}"
export DOTNET_NOLOGO=1

# Build up front so the readiness wait below only covers host start-up, not a
# cold compile (which can take well over the wait on a busy shared runner).
dotnet build "$root/src/Wayfarer.Host" --configuration Release --nologo -v q

setsid dotnet run --project "$root/src/Wayfarer.Host" --no-launch-profile --no-build --configuration Release >/tmp/wayfarer-openapi-export.log 2>&1 &
pid=$!
cleanup() {
  kill -- -"$pid" 2>/dev/null || kill "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
}
trap cleanup EXIT

ready=0
for _ in $(seq 1 300); do
  if ! kill -0 "$pid" 2>/dev/null; then
    break
  fi
  if curl -sf "http://127.0.0.1:${port}/health" >/dev/null; then
    ready=1
    break
  fi
  sleep 0.2
done

if [ "$ready" -ne 1 ]; then
  echo "Host did not become ready. Log:" >&2
  cat /tmp/wayfarer-openapi-export.log >&2 || true
  exit 1
fi

tmp="$(mktemp)"
curl -fsS "http://127.0.0.1:${port}/openapi/v1.json" -o "$tmp"
if [ -n "$(tail -c1 "$tmp")" ]; then
  echo >> "$tmp"
fi

if [ "$mode" = "--check" ]; then
  diff -u "$snapshot" "$tmp"
elif [ -z "$mode" ]; then
  mkdir -p "$(dirname "$snapshot")"
  cp "$tmp" "$snapshot"
else
  echo "Usage: $0 [--check]" >&2
  exit 2
fi
