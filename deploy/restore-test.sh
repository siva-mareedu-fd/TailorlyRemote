#!/usr/bin/env bash
# Monthly restore test skeleton (SDD §13.4): restore the latest backup into a scratch database, check row counts
# for every table, then replay the off-DB deletion log so deleted accounts do not come back.
# Completed in M6.
set -euo pipefail

BACKUP="${1:?usage: restore-test.sh <backup-dir>}"
SCRATCH_DB="tailorly_restore_test"
COMPOSE="docker compose -f $(dirname "$0")/docker-compose.yml"

echo "[restore-test] creating scratch database $SCRATCH_DB"
$COMPOSE exec -T postgres dropdb -U tailorly --if-exists "$SCRATCH_DB"
$COMPOSE exec -T postgres createdb -U tailorly "$SCRATCH_DB"

echo "[restore-test] restoring $BACKUP/tailorly.dump"
$COMPOSE exec -T postgres pg_restore -U tailorly -d "$SCRATCH_DB" --no-owner < "$BACKUP/tailorly.dump"

echo "[restore-test] row counts"
$COMPOSE exec -T postgres psql -U tailorly -d "$SCRATCH_DB" -c \
  "SELECT relname AS table, n_live_tup AS rows FROM pg_stat_user_tables ORDER BY relname;"

# TODO(M6): compare counts with production, verify documents archive, replay the deletion log
#           (run PurgeDeletedAccounts for each listed user id), then drop the scratch database.
echo "[restore-test] done"
