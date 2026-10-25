#!/usr/bin/env bash
# Nightly backup skeleton (SDD §13.4): compressed pg_dump + documents volume, encrypted, shipped off-VM.
# Completed in M6 (encryption key handling, object-storage upload, 30-day retention, failure alert).
set -euo pipefail

COMPOSE="docker compose -f $(dirname "$0")/docker-compose.yml"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="${BACKUP_DIR:-/var/backups/tailorly}/${STAMP}"
mkdir -p "$OUT"

echo "[backup] dumping database"
$COMPOSE exec -T postgres pg_dump -U tailorly -d tailorly --format=custom --compress=9 > "$OUT/tailorly.dump"

echo "[backup] archiving documents volume"
$COMPOSE run --rm --no-deps -v "$OUT:/backup" --entrypoint tar app -czf /backup/documents.tgz -C /data storage

# The deletion log lives outside the database on purpose (SDD §13.4): after restoring an older backup, replay the
# purge for every id it lists. It must be stored off-VM separately from (and newer than) the dumps.
echo "[backup] copying deletion log"
$COMPOSE run --rm --no-deps -v "$OUT:/backup" --entrypoint sh app -c 'cp /data/deletion-log/deletions.log /backup/ 2>/dev/null || true'

# TODO(M6): encrypt with a key held off-VM (e.g. age/gpg), upload to object storage, prune > 30 days,
#           and email the admin on failure.
echo "[backup] done: $OUT"
