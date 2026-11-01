#!/usr/bin/env bash
# Nightly backup (SDD §13.4): compressed pg_dump + documents volume + deletion log, encrypted with age to a public key
# whose private half is held OFF the VM, shipped to object storage with rclone, 30-day retention. Writes a status file
# that the app's AdminAlerts job reads (Alerts__BackupStatusPath), so a failed or missed backup emails the admin.
#
#   deploy/backup.sh                 run from cron, e.g.  15 20 * * *  (01:45 IST)  see deploy/runbooks/operator-runbook.md
#
# Configuration (deploy/.env or the environment):
#   BACKUP_AGE_RECIPIENT   age public key ("age1..."): REQUIRED. Generate the pair off the VM: age-keygen -o tailorly-backup.key
#   BACKUP_REMOTE          rclone destination, e.g. "b2:tailorly-backups" (rclone config on the VM). Empty = local only (warns).
#   BACKUP_DIR             local staging and copies (default /var/backups/tailorly)
#   BACKUP_RETENTION_DAYS  remote retention (default 30); local copies are kept for BACKUP_LOCAL_DAYS (default 3)
set -Eeuo pipefail

DEPLOY_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=deploy/compose.sh
. "$DEPLOY_DIR/compose.sh"   # COMPOSE=(docker compose -f … [-f docker-compose.prod.yml when TAILORLY_PROD=1])

BACKUP_DIR="${BACKUP_DIR:-/var/backups/tailorly}"
RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-30}"
LOCAL_DAYS="${BACKUP_LOCAL_DAYS:-3}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
WORK="$BACKUP_DIR/$STAMP"
STEP="start"

log() { echo "[backup $(date -u +%H:%M:%S)] $*"; }

# The status file lives in the app's ops volume (/data/ops). Written through the app image so ownership is right.
write_status() {
  local status="$1" last_ok
  last_ok="$(cat "$BACKUP_DIR/.last-success" 2>/dev/null || echo null)"
  [[ "$last_ok" == "null" ]] || last_ok="\"$last_ok\""
  local json
  json="{\"status\":\"$status\",\"at\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",\"step\":\"$STEP\",\"lastSuccessAt\":$last_ok,\"stamp\":\"$STAMP\"}"
  echo "$json" > "$BACKUP_DIR/status.json"
  echo "$json" | "${COMPOSE[@]}" run --rm --no-deps -T --entrypoint sh app -c 'cat > /data/ops/backup-status.json' >/dev/null 2>&1 \
    || log "WARNING: could not write the status file into the ops volume"
}

on_error() {
  local code=$?
  log "FAILED at step '$STEP' (exit $code)"
  write_status failed
  exit "$code"
}
trap on_error ERR

[[ -n "${BACKUP_AGE_RECIPIENT:-}" ]] || { STEP="config"; log "BACKUP_AGE_RECIPIENT is not set"; false; }
command -v age >/dev/null || { STEP="config"; log "age is not installed (apt install age)"; false; }
if [[ -n "${BACKUP_REMOTE:-}" ]]; then command -v rclone >/dev/null || { STEP="config"; log "rclone is not installed"; false; }; fi
mkdir -p "$WORK"
chmod 700 "$BACKUP_DIR" "$WORK"

STEP="pg_dump"
log "dumping database"
# pg_dump runs inside a transaction that exported its snapshot, and the row counts are read in that same transaction,
# so they describe exactly what the dump holds (Hangfire's tables change every few seconds). The restore test compares
# them with the restored database. The plain dump exists in the postgres container only until it is encrypted.
DUMP_IN_PG="/tmp/tailorly-backup-$STAMP.dump"
"${COMPOSE[@]}" exec -T postgres psql -U tailorly -d tailorly -qAt -F, -v ON_ERROR_STOP=1 > "$WORK/psql.out" <<SQL
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT pg_export_snapshot() AS snap \gset
\setenv SNAP :snap
\! pg_dump -U tailorly -d tailorly --snapshot="\$SNAP" --format=custom --compress=9 -f $DUMP_IN_PG && echo DUMP_OK
SELECT table_schema || '.' || table_name,
       (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', table_schema, table_name), false, true, '')))[1]::text
FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema IN ('public', 'hangfire') ORDER BY 1;
COMMIT;
SQL
grep -qx DUMP_OK "$WORK/psql.out"
grep -vx DUMP_OK "$WORK/psql.out" > "$WORK/row-counts.csv" && rm "$WORK/psql.out"
"${COMPOSE[@]}" exec -T postgres cat "$DUMP_IN_PG" | age -r "$BACKUP_AGE_RECIPIENT" -o "$WORK/tailorly.dump.age"
"${COMPOSE[@]}" exec -T postgres rm -f "$DUMP_IN_PG"

STEP="documents"
log "archiving documents volume"
"${COMPOSE[@]}" run --rm --no-deps -T --entrypoint tar app -czf - -C /data storage | age -r "$BACKUP_AGE_RECIPIENT" -o "$WORK/documents.tgz.age"

STEP="deletion-log"
log "copying deletion log"
"${COMPOSE[@]}" run --rm --no-deps -T --entrypoint sh app -c 'cat /data/deletion-log/deletions.log 2>/dev/null || true' \
  > "$WORK/deletions.log"

STEP="manifest"
( cd "$WORK" && sha256sum ./*.age deletions.log row-counts.csv > SHA256SUMS )
log "sizes: $(du -sh "$WORK" | cut -f1) total"

if [[ -n "${BACKUP_REMOTE:-}" ]]; then
  STEP="upload"
  log "uploading to $BACKUP_REMOTE/$STAMP"
  rclone copy "$WORK" "$BACKUP_REMOTE/$STAMP" --immutable
  rclone check "$WORK" "$BACKUP_REMOTE/$STAMP" --one-way >/dev/null
  # The deletion log also has its own always-current copy (deploy/sync-deletion-log.sh); keep that one in step.
  "$DEPLOY_DIR/sync-deletion-log.sh" "$WORK/deletions.log"

  STEP="prune-remote"
  log "removing remote backups older than $RETENTION_DAYS days"
  rclone delete "$BACKUP_REMOTE" --min-age "${RETENTION_DAYS}d" --exclude "deletion-log/**"
  rclone rmdirs "$BACKUP_REMOTE" --leave-root
else
  log "WARNING: BACKUP_REMOTE is empty: this backup stays on the VM and is lost with it"
fi

STEP="prune-local"
find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -name '20*Z' -mtime +"$LOCAL_DAYS" -exec rm -rf {} +

STEP="done"
date -u +%Y-%m-%dT%H:%M:%SZ > "$BACKUP_DIR/.last-success"
write_status ok
log "done: $STAMP"
