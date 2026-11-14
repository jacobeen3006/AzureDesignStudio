#!/usr/bin/env bash
# Launches the built macOS app (Mac Catalyst) against a fixture copy of workspace.db in a scratch
# data directory and checks that the startup did what it should.
#
#   src/VerifyDesktopWorkspace.sh <l2|l3|l4|corrupt> [--keep] [path-to-.app]
#
#   l2 | l3 | l4   a legacy workspace built from Core.Tests/Fixtures; expects a migration history row
#                  and a premigration backup under backups/ (l4 also the upgrade-removed-rows report)
#   corrupt        the L3 fixture truncated to a non-page-multiple length; expects a
#                  workspace.corrupt-*.db beside a new workspace.db
#
# The app is started with ADS_DATA_DIR pointing at a throwaway folder (mktemp -d), so the real
# ~/Library/Application Support data is never read or written. The app is stopped and the folder
# removed afterwards unless --keep is given. Needs a macOS GUI session and the sqlite3 CLI.
# Exit code: 0 outcome reached, 1 outcome not reached within 60 seconds, 2 usage or setup problem.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
fixtures="$repo/src/AzureDesignStudio.Core.Tests/Fixtures"

scenario="${1:-}"
shift || true
keep=0
app=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --keep) keep=1; shift ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) app="$1"; shift ;;
  esac
done

case "$scenario" in
  l2) fixture="$fixtures/workspace-legacy-L2-pre-governance.sql" ;;
  l3 | corrupt) fixture="$fixtures/workspace-legacy-L3-current-legacy.sql" ;;
  l4) fixture="$fixtures/workspace-legacy-L4-duplicates.sql" ;;
  *) echo "usage: $0 <l2|l3|l4|corrupt> [--keep] [path-to-.app]" >&2; exit 2 ;;
esac

if [[ -z "$app" ]]; then
  for candidate in "$repo"/src/AzureDesignStudio.Desktop/bin/Debug/net10.0-maccatalyst/*/"Azure Design Studio.app"; do
    [[ -d "$candidate" ]] && app="$candidate" && break
  done
fi
if [[ -z "$app" || ! -d "$app" ]]; then
  echo "Built app not found; run: dotnet build src/AzureDesignStudio.Desktop -f net10.0-maccatalyst" >&2
  exit 2
fi
exe_name="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Contents/Info.plist")"
exe="$app/Contents/MacOS/$exe_name"
[[ -x "$exe" ]] || { echo "Executable not found: $exe" >&2; exit 2; }
command -v sqlite3 >/dev/null || { echo "sqlite3 CLI not found" >&2; exit 2; }

scratch="$(mktemp -d "${TMPDIR:-/tmp}/ads-verify-workspace.XXXXXX")"
pid=""
cleanup() {
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    kill "$pid" 2>/dev/null || true
    sleep 1
    kill -9 "$pid" 2>/dev/null || true
  fi
  if [[ "$keep" -eq 0 ]]; then rm -rf "$scratch" "$scratch.app.log"; else echo "Scratch directory kept: $scratch"; fi
}
trap cleanup EXIT

sqlite3 "$scratch/workspace.db" < "$fixture"
if [[ "$scenario" == "corrupt" ]]; then
  # A SQLite file is a whole number of pages; cutting it elsewhere makes the integrity check fail.
  size="$(wc -c < "$scratch/workspace.db" | tr -d ' ')"
  head -c "$((size - size % 4096 - 1000))" "$scratch/workspace.db" > "$scratch/workspace.db.cut"
  mv "$scratch/workspace.db.cut" "$scratch/workspace.db"
fi
echo "Scenario: $scenario  data dir: $scratch"

ADS_DATA_DIR="$scratch" "$exe" >"$scratch.app.log" 2>&1 &
pid=$!

# A plain connection, not -readonly: a read-only open of a WAL database that has no -shm file fails
# with "unable to open database file", which looked like an upgrade that never finished.
history_rows() {
  sqlite3 -cmd ".timeout 2000" "$scratch/workspace.db" 'SELECT COUNT(*) FROM "__EFMigrationsHistory"' 2>/dev/null || echo 0
}

reached=0
for _ in $(seq 1 60); do
  if ! kill -0 "$pid" 2>/dev/null; then echo "The app exited early (see $scratch.app.log)"; break; fi
  case "$scenario" in
    corrupt)
      if compgen -G "$scratch/workspace.corrupt-*.db" >/dev/null && [[ -s "$scratch/workspace.db" ]] \
         && [[ "$(history_rows)" -gt 0 ]]; then reached=1; fi ;;
    l2 | l3)
      if [[ "$(history_rows)" -gt 0 ]] && compgen -G "$scratch/backups/*premigration*.db" >/dev/null; then reached=1; fi ;;
    l4)
      if [[ "$(history_rows)" -gt 0 ]] && compgen -G "$scratch/backups/*premigration*.db" >/dev/null \
         && compgen -G "$scratch/backups/upgrade-removed-rows-*.jsonl" >/dev/null; then reached=1; fi ;;
  esac
  [[ "$reached" -eq 1 ]] && break
  sleep 1
done

echo "--- scratch directory listing ---"
(cd "$scratch" && find . -type f | sort | sed 's|^\./||')
echo "--- workspace.db ---"
echo "history rows: $(history_rows)"
for table in Designs Scans ChangeFeed; do
  echo "$table rows: $(sqlite3 -cmd ".timeout 2000" "$scratch/workspace.db" "SELECT COUNT(*) FROM \"$table\"" 2>/dev/null || echo n/a)"
done

if [[ "$reached" -eq 1 ]]; then
  echo "OUTCOME REACHED: $scenario"
  exit 0
fi
echo "OUTCOME NOT REACHED within 60 seconds: $scenario" >&2
exit 1
