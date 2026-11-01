# Restore runbook

How to prove the backups work (monthly) and how to restore production (rare). Targets (SDD §13.4): **RPO ≤ 24 h**
(nightly backup; deletions are shipped every 15 minutes), **RTO ≤ 4 h**.

What a backup holds (`BACKUP_REMOTE/<YYYYMMDDTHHMMSSZ>/`, 30 days):

| File | Content |
|---|---|
| `tailorly.dump.age` | `pg_dump --format=custom` of the whole database (app tables and Hangfire), age-encrypted |
| `documents.tgz.age` | the `documents` volume (résumés, tailored files), age-encrypted |
| `deletions.log` | the deletion log at backup time (`userId,UTC time` per line; ids only) |
| `row-counts.csv` | every table's row count, taken in the same snapshot as the dump |
| `SHA256SUMS` | checksums of the above |

Plus `BACKUP_REMOTE/deletion-log/deletions.log`, the always-current deletion log (union of every copy; never shrinks).

You need the **age private key** (`tailorly-backup.key`, in the password manager). It never stays on the VM.

## 1. Why the deletion log exists
A restore puts the database back as it was at backup time. Anyone who deleted their account **after** that backup
would come back, files included. The deletion log lives outside the database, so a restore cannot roll it back. After
every restore, **replay it**: each listed account that exists again is purged again, files first, exactly like the
first time. Skipping this step breaks the product's deletion promise.

## 2. Monthly restore test (no effect on production)
```bash
cd /opt/tailorly
install -m 600 /dev/stdin /tmp/tailorly-backup.key      # paste the private key, then Ctrl-D
BACKUP_AGE_IDENTITY=/tmp/tailorly-backup.key deploy/restore-test.sh          # newest backup; or pass a stamp
shred -u /tmp/tailorly-backup.key
```
`restore-test.sh` restores into a **scratch** database (`tailorly_restore_test`) and a scratch documents volume, and:
1. verifies the checksums;
2. compares every table's row count with `row-counts.csv` (any difference fails the test);
3. checks that every document the database references is in the archive;
4. replays the deletion log (backup copy plus the current remote copy) against the scratch copy with
   `Tailorly.App --replay-deletion-log`, and checks that none of the listed accounts is left;
5. drops the scratch database and volume.

Record its last line (`RESULT ok: backup …, N tables, N users, N documents, N deletion(s) replayed`) in the table at the
end of this file, with the date and who ran it. A failure is an incident: fix the backup before anything else.

`tests/ops/backup-restore.sh` runs the whole cycle on a local stack (backup, a deletion after it, restore test); use it
after changing any of these scripts.

## 3. Restoring production
Use this when the database or the documents volume is lost or corrupted. If only the app misbehaves, roll back the
image instead (operator runbook §2.1).

1. **Announce** maintenance if users are active. Note the time: data written after the chosen backup is lost (RPO).
2. **Stop the app** (Postgres stays up):
   ```bash
   cd /opt/tailorly
   alias dc='docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.prod.yml'
   dc stop app caddy
   ```
3. **Keep what is there** in case you need it: `dc exec -T postgres pg_dump -U tailorly -d tailorly -Fc > /var/backups/tailorly/pre-restore.dump`
   (skip if the database is gone).
4. **Fetch and decrypt** the chosen backup (newest unless it is the bad one):
   ```bash
   STAMP=$(rclone lsf "$BACKUP_REMOTE" --dirs-only | grep -E '^20' | sort | tail -1 | tr -d /)
   mkdir -p /tmp/restore && rclone copy "$BACKUP_REMOTE/$STAMP" /tmp/restore
   (cd /tmp/restore && sha256sum -c SHA256SUMS)
   install -m 600 /dev/stdin /tmp/tailorly-backup.key     # paste the private key
   ```
5. **Restore the database** into a fresh `tailorly`:
   ```bash
   dc exec -T postgres psql -U tailorly -d postgres -c "DROP DATABASE IF EXISTS tailorly WITH (FORCE)" -c "CREATE DATABASE tailorly"
   age -d -i /tmp/tailorly-backup.key /tmp/restore/tailorly.dump.age | dc exec -T postgres pg_restore -U tailorly -d tailorly --no-owner --exit-on-error
   ```
6. **Restore the documents** (replaces the volume's content):
   ```bash
   age -d -i /tmp/tailorly-backup.key /tmp/restore/documents.tgz.age \
     | dc run --rm --no-deps -T --user root --entrypoint sh app -c 'rm -rf /data/storage/* && tar -xzf - -C /data && chown -R "$APP_UID" /data/storage'
   ```
7. **Migrate** (the image may be newer than the backup): `dc run --rm migrate`.
8. **Replay the deletion log**: the current remote copy is the complete one.
   ```bash
   rclone cat "$BACKUP_REMOTE/deletion-log/deletions.log" > /tmp/restore/deletions.log
   cat /tmp/restore/deletions.log | dc run --rm --no-deps -T --entrypoint sh app -c 'cat >> /data/deletion-log/deletions.log'
   dc run --rm --no-deps -e Hangfire__ServerEnabled=false app --replay-deletion-log
   ```
   The last line says how many listed accounts were purged again. Check:
   `dc exec postgres psql -U tailorly -d tailorly -c "SELECT count(*) FROM asp_net_users WHERE id IN (…ids…)"` → 0.
9. **Start** and smoke-test: `TAG=$(tail -1 deploy/.deployed-tags | cut -d' ' -f2) deploy/deploy.sh` (it migrates again,
   which is a no-op, and runs the smoke test). Then Admin → Status.
10. **Clean up**: `shred -u /tmp/tailorly-backup.key && rm -rf /tmp/restore`. Take a fresh backup: `deploy/backup.sh`.
11. **Tell users** if data was lost (the window since the backup). Note it in the incident log.

## 4. Losing the whole VM
Build a new VM (operator runbook §1.1–1.3), restore `deploy/.env` from the password manager, `rclone config` again,
then follow §3 from step 4 (start Postgres first: `dc up -d --wait postgres`). The Data Protection keys (`dpkeys`) are
not in the backup: every user signs in again and email links issued before the loss stop working (users ask for new
ones). Point DNS at the new VM; Caddy gets a new certificate.

## 5. Restore test log
| Date | Who | Backup | Result line |
|---|---|---|---|
| 2026-11-01 | Claude Code (local stack, `tests/ops/backup-restore.sh`) | 20261101T161818Z | `RESULT ok: backup 20261101T161818Z, 45 tables, 25 users, 15 documents, 10 deletion(s) replayed` (one account deleted after the backup was purged again) |
