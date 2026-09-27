#!/usr/bin/env bash
# The interactive API UI must not be served when the host environment is Production.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
port="${WAYFARER_OPENAPI_PORT:-5089}"

export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS="http://127.0.0.1:${port}"
export DOTNET_NOLOGO=1
unset OpenApi__ExposeUi || true

setsid dotnet run --project "$root/src/Wayfarer.Host" --no-launch-profile --configuration Release >/tmp/wayfarer-openapi-production.log 2>&1 &
pid=$!
cleanup() {
  kill -- -"$pid" 2>/dev/null || kill "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
}
trap cleanup EXIT

ready=0
for _ in $(seq 1 50); do
  if curl -sf "http://127.0.0.1:${port}/health" >/dev/null; then
    ready=1
    break
  fi
  sleep 0.2
done

if [ "$ready" -ne 1 ]; then
  echo "Host did not become ready. Log:" >&2
  cat /tmp/wayfarer-openapi-production.log >&2 || true
  exit 1
fi

assert_status() {
  local path="$1"
  local expected="$2"
  local actual
  actual="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${port}${path}")"
  if [ "$actual" != "$expected" ]; then
    echo "Expected ${expected} from ${path} in Production, got ${actual}" >&2
    exit 1
  fi
}

assert_status /swagger 404
assert_status /swagger/index.html 404
assert_status /openapi/v1.json 200
