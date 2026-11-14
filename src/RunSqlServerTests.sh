#!/usr/bin/env bash
# Runs the SQL Server migration tests (Category=SqlServer) against a throwaway SQL Server 2022
# container: start it, wait until it accepts logins, run the tests, remove it.
#
#   src/RunSqlServerTests.sh
#
# Environment: SQLTEST_PORT (host port, default 14333), SQLTEST_WAIT (seconds to wait for the
# server, default 240; an emulated start can need more), CONFIGURATION (default Debug).
# Needs: Docker and the .NET SDK from global.json. On Apple silicon the image runs under amd64
# emulation (--platform linux/amd64).
# The SA password below is an ephemeral credential for a container that lives only for this run.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
port="${SQLTEST_PORT:-14333}"
configuration="${CONFIGURATION:-Debug}"
wait_seconds="${SQLTEST_WAIT:-240}"
image="mcr.microsoft.com/mssql/server:2022-latest"
name="ads-sqltest-$$"
password="Ads_Test_Only_1!"

platform=()
case "$(uname -m)" in
  arm64|aarch64) platform=(--platform linux/amd64) ;;
esac

cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT

# ${platform[@]+...}: an empty array is an unbound variable under `set -u` in bash 3.2 (macOS).
docker run -d --name "$name" ${platform[@]+"${platform[@]}"} \
  -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$password" \
  -p "127.0.0.1:$port:1433" "$image" >/dev/null

echo "Waiting for SQL Server in $name (up to $wait_seconds s)..."
ready=0
for _ in $(seq 1 $((wait_seconds / 2))); do
  # Read the log into a variable first: `docker logs | grep -q` under pipefail fails with SIGPIPE
  # whenever grep matches and exits before docker finishes writing.
  logs="$(docker logs "$name" 2>&1 || true)"
  if grep -q "SQL Server is now ready for client connections" <<<"$logs"; then
    ready=1
    break
  fi
  if [ "$(docker inspect -f '{{.State.Running}}' "$name" 2>/dev/null || echo false)" != "true" ]; then
    break
  fi
  sleep 2
done
if [ "$ready" -ne 1 ]; then
  echo "SQL Server did not become ready:" >&2
  docker logs --tail 40 "$name" >&2 || true
  exit 1
fi

export ADS_TEST_SQLSERVER="Server=127.0.0.1,$port;User ID=sa;Password=$password;TrustServerCertificate=True;Encrypt=False;"

cd "$repo"
dotnet test src/AzureDesignStudio.Server.Tests -c "$configuration" -p:RunAOTCompilation=false --filter "Category=SqlServer"
