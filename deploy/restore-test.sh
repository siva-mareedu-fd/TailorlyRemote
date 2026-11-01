#!/usr/bin/env bash
# Monthly restore test (SDD §13.4, DatabaseDesign §10): restores a backup into a SCRATCH database and a scratch
# documents folder (production is never touched), compares every table's row count with the counts taken at dump time,
# checks the documents archive, replays the deletion log against the scratch copy, then removes it.
#
#   BACKUP_AGE_IDENTITY=/path/to/tailorly-backup.key deploy/restore-test.sh [STAMP|DIR]
#     STAMP  a backup folder name in BACKUP_REMOTE (default: the newest); DIR  a local backup folder
#
# The age identity (private key) is kept off the VM; bring it for the test and remove it afterwards (shred -u).
# Exit code 0 = restorable. The result line is meant for the restore log in deploy/runbooks/restore-runbook.md.
set -Eeuo pipefail

DEPLOY_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=deploy/compose.sh
. "$DEPLOY_DIR/compose.sh"

SCRATCH_DB="tailorly_restore_test"
WORK="$(mktemp -d /tmp/tailorly-restore.XXXXXX)"
SCRATCH_VOLUME="tailorly_restore_test_storage"
log() { echo "[restore-test $(date -u +%H:%M:%S)] $*"; }
fail() { log "FAILED: $*"; exit 1; }
cleanup() {
  "${COMPOSE[@]}" exec -T postgres dropdb -U tailorly --if-exists "$SCRATCH_DB" >/dev/null 2>&1 || true
  docker volume rm -f "$SCRATCH_VOLUME" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

[[ -n "${BACKUP_AGE_IDENTITY:-}" && -f "$BACKUP_AGE_IDENTITY" ]] || fail "set BACKUP_AGE_IDENTITY to the age private key file"
command -v age >/dev/null || fail "age is not installed"

# 1. Fetch
SRC="${1:-}"
if [[ -n "$SRC" && -d "$SRC" ]]; then
  cp -r "$SRC"/. "$WORK/"
else
  [[ -n "${BACKUP_REMOTE:-}" ]] || fail "BACKUP_REMOTE is empty; pass a local backup folder instead"
  [[ -n "$SRC" ]] || SRC="$(rclone lsf "$BACKUP_REMOTE" --dirs-only | grep -E '^20[0-9]{6}T[0-9]{6}Z/$' | sort | tail -n 1 | tr -d /)"
  [[ -n "$SRC" ]] || fail "no backup found in $BACKUP_REMOTE"
  log "fetching $BACKUP_REMOTE/$SRC"
  rclone copy "$BACKUP_REMOTE/$SRC" "$WORK"
fi
( cd "$WORK" && sha256sum -c SHA256SUMS --quiet ) || fail "checksums do not match"
log "backup $(basename "${SRC:-local}") verified ($(du -sh "$WORK" | cut -f1))"

# 2. Database → scratch
log "restoring into $SCRATCH_DB"
"${COMPOSE[@]}" exec -T postgres dropdb -U tailorly --if-exists "$SCRATCH_DB"
"${COMPOSE[@]}" exec -T postgres createdb -U tailorly "$SCRATCH_DB"
age -d -i "$BACKUP_AGE_IDENTITY" "$WORK/tailorly.dump.age" \
  | "${COMPOSE[@]}" exec -T postgres pg_restore -U tailorly -d "$SCRATCH_DB" --no-owner --exit-on-error

# 3. Row counts: every table counted at dump time must exist with the same count.
"${COMPOSE[@]}" exec -T postgres psql -U tailorly -d "$SCRATCH_DB" -At -F, -c \
  "SELECT table_schema || '.' || table_name, (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', table_schema, table_name), false, true, '')))[1]::text
   FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema IN ('public', 'hangfire') ORDER BY 1" \
  > "$WORK/restored-counts.csv"
mismatches=0
while IFS=, read -r table expected; do
  actual="$(grep -E "^${table//./\\.}," "$WORK/restored-counts.csv" | cut -d, -f2 || true)"
  if [[ "$actual" != "$expected" ]]; then
    log "  MISMATCH $table: dumped $expected, restored ${actual:-missing}"
    mismatches=$((mismatches + 1))
  fi
done < "$WORK/row-counts.csv"
tables="$(wc -l < "$WORK/row-counts.csv")"
users="$(grep -E '^public\.asp_net_users,' "$WORK/restored-counts.csv" | cut -d, -f2)"
[[ "$mismatches" -eq 0 ]] || fail "$mismatches table(s) differ"
log "row counts match for $tables tables ($users users)"

# 4. Documents → scratch volume; every stored key must be in the archive.
docker volume create "$SCRATCH_VOLUME" >/dev/null
# Extracted with the app image as root, then handed to the app user so the replay below can delete files.
age -d -i "$BACKUP_AGE_IDENTITY" "$WORK/documents.tgz.age" \
  | "${COMPOSE[@]}" run --rm --no-deps -T --user root -v "$SCRATCH_VOLUME:/restore" --entrypoint sh app \
      -c 'tar -xzf - -C /restore && chown -R "$APP_UID" /restore'
files="$(docker run --rm -v "$SCRATCH_VOLUME:/restore:ro" alpine:3 sh -c 'find /restore/storage -type f | wc -l')"
missing="$("${COMPOSE[@]}" exec -T postgres psql -U tailorly -d "$SCRATCH_DB" -At -c \
  "SELECT storage_key FROM resumes UNION ALL SELECT storage_key FROM tailored_documents WHERE storage_key IS NOT NULL" \
  | docker run --rm -i -v "$SCRATCH_VOLUME:/restore:ro" alpine:3 sh -c 'n=0; while read -r k; do [ -f "/restore/storage/$k" ] || n=$((n+1)); done; echo $n')"
[[ "$missing" -eq 0 ]] || fail "$missing stored document(s) referenced by the database are not in the archive"
log "documents: $files file(s), every database reference present"

# 5. Replay the deletion log against the scratch copy (restore runbook step), then prove those accounts are gone.
cp "$WORK/deletions.log" "$WORK/replay.log"
if [[ -n "${BACKUP_REMOTE:-}" ]]; then
  rclone cat "$BACKUP_REMOTE/deletion-log/deletions.log" >> "$WORK/replay.log" 2>/dev/null || true # the newest copy
fi
sort -u -o "$WORK/replay.log" "$WORK/replay.log"
docker run --rm -i -v "$SCRATCH_VOLUME:/restore" --entrypoint sh alpine:3 -c 'cat > /restore/deletions.log' < "$WORK/replay.log"
"${COMPOSE[@]}" run --rm --no-deps -T \
  -e "ConnectionStrings__Default=Host=postgres;Port=5432;Database=$SCRATCH_DB;Username=tailorly;Password=$POSTGRES_PASSWORD" \
  -e Hangfire__ServerEnabled=false -e Storage__RootPath=/restore/storage -e DeletionLog__Path=/restore/deletions.log \
  -v "$SCRATCH_VOLUME:/restore" app --replay-deletion-log /restore/deletions.log
listed="$(cut -d, -f1 "$WORK/replay.log" | grep -cE '^[0-9a-f-]{36}$' || true)"
if [[ "$listed" -gt 0 ]]; then
  ids="$(cut -d, -f1 "$WORK/replay.log" | grep -E '^[0-9a-f-]{36}$' | sed "s/.*/'&'/" | paste -sd,)"
  back="$("${COMPOSE[@]}" exec -T postgres psql -U tailorly -d "$SCRATCH_DB" -At -c "SELECT count(*) FROM asp_net_users WHERE id IN ($ids)")"
  [[ "$back" -eq 0 ]] || fail "$back deleted account(s) still present after the replay"
fi
log "deletion log: $listed id(s) replayed, none present afterwards"

log "RESULT ok: backup ${SRC:-local}, $tables tables, $users users, $files documents, $listed deletion(s) replayed"
