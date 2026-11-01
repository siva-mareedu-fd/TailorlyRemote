#!/usr/bin/env bash
# Ships the off-DB deletion log (SDD §13.4) to the backup bucket, every 15 minutes from cron:
#   */15 * * * *  /opt/tailorly/deploy/sync-deletion-log.sh
# The remote copy is the union of what is already there and the local file, so a lost or truncated local log never
# shrinks it. It holds user ids and times only. Optional argument: a local copy to use instead of the volume.
set -Eeuo pipefail
DEPLOY_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=deploy/compose.sh
. "$DEPLOY_DIR/compose.sh"

[[ -n "${BACKUP_REMOTE:-}" ]] || { echo "[deletion-log] BACKUP_REMOTE is not set; nothing shipped" >&2; exit 1; }
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

if [[ -n "${1:-}" ]]; then cp "$1" "$TMP/local.log"
else "${COMPOSE[@]}" run --rm --no-deps -T --entrypoint sh app -c 'cat /data/deletion-log/deletions.log 2>/dev/null || true' > "$TMP/local.log"
fi
rclone cat "$BACKUP_REMOTE/deletion-log/deletions.log" > "$TMP/remote.log" 2>/dev/null || : > "$TMP/remote.log"
sort -u "$TMP/remote.log" "$TMP/local.log" | grep -E '^[0-9a-f-]{36},' > "$TMP/merged.log" || true
if ! cmp -s "$TMP/merged.log" <(sort -u "$TMP/remote.log"); then
  rclone copyto "$TMP/merged.log" "$BACKUP_REMOTE/deletion-log/deletions.log"
  echo "[deletion-log] shipped $(wc -l < "$TMP/merged.log") entries"
fi
