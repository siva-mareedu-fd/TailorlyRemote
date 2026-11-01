# Operator runbook: Tailorly Remote on one VM

For the person who runs production. The architecture is in `docs/SystemDesign_TailorlyRemote.md` §13–§15; this file
is the how-to. Related: [restore runbook](restore-runbook.md), [on-call checklist](on-call-checklist.md).

Production is one VM running Docker Compose: `caddy` (TLS, SPA, reverse proxy; the only service with ports), `app`
(API and Hangfire in one process), `postgres`, and a one-shot `migrate`. Everything persistent is in named volumes:
`pgdata`, `documents`, `dpkeys` (cookie keys), `deletionlog`, `ops` (backup status), `caddy_data` (certificates).

## 1. First install

### 1.1 The VM
- Ubuntu 24.04 LTS (or Debian 12), 2–4 GB RAM, 2 vCPU, 40 GB disk. A DNS `A`/`AAAA` record for the domain points at it.
- Firewall: inbound 22 (from your IP only), 80 and 443. Nothing else. Postgres is never exposed.
- Install: Docker Engine with the Compose plugin (v2.24+), `git`, `age`, `rclone`, `curl`.
  ```bash
  sudo apt-get update && sudo apt-get install -y git age rclone curl
  curl -fsSL https://get.docker.com | sudo sh
  sudo adduser --disabled-password deploy && sudo usermod -aG docker deploy
  ```
- Time zone of the VM does not matter (containers run in UTC; users' times use IANA zones).

### 1.2 The checkout and configuration (as `deploy`)
```bash
sudo mkdir -p /opt/tailorly && sudo chown deploy: /opt/tailorly
git clone https://github.com/<owner>/TailorlyRemote.git /opt/tailorly
cd /opt/tailorly && cp deploy/.env.example deploy/.env && chmod 600 deploy/.env
```
Edit `deploy/.env`. Required in production:

| Key | Value |
|---|---|
| `TAILORLY_PROD` | `1` (the scripts add `docker-compose.prod.yml`: registry images, memory limits, MFA forced on) |
| `SITE_ADDRESS` | the public domain, e.g. `app.tailorly.in` (Caddy gets the certificate by ACME) |
| `IMAGE_REPO` | `ghcr.io/<owner>` (lower case), where CI pushes images |
| `POSTGRES_PASSWORD` | `openssl rand -hex 24` |
| `Security__IpHashKey` | `openssl rand -hex 32` |
| `Ai__Provider`, `Anthropic__ApiKey` | `Anthropic` and the production key (separate from staging and dev keys) |
| `Email__*` | the transactional email provider's SMTP settings; `Email__From` on the authenticated domain (SPF, DKIM, DMARC) |
| `Admin__BootstrapEmail` | the first admin's email |
| `BACKUP_AGE_RECIPIENT`, `BACKUP_REMOTE` | see §1.4 |
| `SMOKE_EMAIL`, `SMOKE_PASSWORD` | a verified, non-admin account used only by the deploy smoke test (create it after the first deploy) |

Never commit `deploy/.env`. Keep a copy of it in your password manager.

### 1.3 Registry access
CI pushes `tailorly-app` and `tailorly-web` to GHCR tagged with the git SHA. If the packages are private, log the VM in
once with a token that can only read packages:
```bash
echo "<read:packages token>" | docker login ghcr.io -u <github-user> --password-stdin
```

### 1.4 Backups (before the first user)
1. On **your own machine** (not the VM): `age-keygen -o tailorly-backup.key`. Store the file in your password manager
   and on one offline medium. The line `# public key: age1…` is `BACKUP_AGE_RECIPIENT`. Without the private key no
   backup can be read: this is deliberate, so a stolen VM or bucket leaks nothing.
2. Create an object-storage bucket (B2, S3, R2, …) with a key limited to that bucket. Prefer object lock or versioning
   with a 30-day minimum, so an attacker on the VM cannot delete old backups.
3. On the VM: `rclone config` → a remote, e.g. `b2`. Set `BACKUP_REMOTE=b2:tailorly-backups` in `deploy/.env`.
4. Cron for `deploy` (`crontab -e`). Times are UTC:
   ```cron
   15 20 * * *  cd /opt/tailorly && deploy/backup.sh >> /var/log/tailorly-backup.log 2>&1        # 01:45 IST
   */15 * * * * cd /opt/tailorly && deploy/sync-deletion-log.sh >> /var/log/tailorly-backup.log 2>&1
   ```
   `sudo touch /var/log/tailorly-backup.log && sudo chown deploy: /var/log/tailorly-backup.log`, and rotate it with
   logrotate (weekly, 8 copies).
5. Run `deploy/backup.sh` once by hand after the first deploy. The app checks `/data/ops/backup-status.json`: no file,
   a failed run or no success for 26 hours emails the admin.

### 1.5 First deploy
The first image comes from CI (push to `main`), or build and push one yourself. Then:
```bash
cd /opt/tailorly && git checkout --detach <sha> && TAG=<sha> deploy/deploy.sh
```
`deploy.sh`:
1. pulls `tailorly-app:<sha>` and `tailorly-web:<sha>`;
2. takes a backup if Postgres is already running and backups are configured;
3. **runs the migrations first**: `docker compose run --rm migrate` (`Tailorly.App --migrate`: EF migrations, the
   Hangfire schema and the idempotent seed). It must finish before the new app starts. The app never migrates itself;
4. starts `app` and `caddy` and waits for their health checks;
5. smoke-tests `/healthz`, `/readyz`, `/api/v1/meta`, the SPA and, with `SMOKE_EMAIL`, one scripted sign-in;
6. appends the tag to `deploy/.deployed-tags`.

### 1.6 The first admin
1. Sign up at `https://<domain>` with `Admin__BootstrapEmail` and verify the email.
2. Grant the role: `docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.prod.yml run --rm migrate --seed`
   (the seed gives the Admin role to that address once the account exists).
3. Sign in, open **Account → Two-step sign-in**, set it up with an authenticator app, and save the recovery codes in
   the password manager. Production refuses the admin console until this is done (`403 MFA_NOT_ENABLED`).
4. Admin → **Flags**: decide `signup.require_invite_code` (on for a closed beta), create invite codes.
5. Admin → **Companies** → **Load seed list**; Admin → **Sources** → **Run now** on Greenhouse.
6. Create the smoke-test account (a normal user, verified) and put it in `deploy/.env`.

### 1.7 CI deployment
`.github/workflows/ci.yml` publishes images on every push to `main` and, when the repository variable
`DEPLOY_ENABLED=true`, runs `deploy.sh` on the VM over SSH. Set up a GitHub environment `production` (add required
reviewers to approve each deploy) with:
- variables `DEPLOY_HOST`, `DEPLOY_USER` (`deploy`), `DEPLOY_PATH` (`/opt/tailorly`);
- secrets `DEPLOY_SSH_KEY` (a new key pair; the public half in `~deploy/.ssh/authorized_keys`) and
  `DEPLOY_KNOWN_HOSTS` (`ssh-keyscan <host>` output, checked against the VM's fingerprint).

## 2. Routine operations

| When | What |
|---|---|
| Every deploy | Watch the deploy job, then Admin → **Status**. |
| Daily (2 min) | Admin → Status: sources green, digests last 24 h with 0 failed / 0 unsent, AI errors, disk. |
| Weekly | `/var/log/tailorly-backup.log` shows nightly `done`. Admin → Usage: no user near a cap by abuse. Dependabot PRs. |
| Monthly | **Restore test** (restore runbook §2) and record the result line. Check `docker system df`; `docker image prune -a --filter "until=720h"`. Review Admin → Users for suspended or odd accounts. |
| Quarterly | Rotate the Anthropic key and the SMTP password; re-read each enabled source's terms (`terms_reviewed_at`). |

### 2.1 Rollback
```bash
cd /opt/tailorly && deploy/deploy.sh --rollback        # re-deploys the previous tag in deploy/.deployed-tags
```
Migrations are backward compatible (expand → migrate → contract), so the old image runs on the new schema. Check out
the matching git SHA too if you need that version's scripts.

### 2.2 Useful commands
```bash
alias dc='docker compose -f /opt/tailorly/deploy/docker-compose.yml -f /opt/tailorly/deploy/docker-compose.prod.yml'
dc ps                                   # health of every service
dc logs --since 1h app | jq -c 'select(."@l" == "Error" or ."@l" == "Warning")'   # JSON logs: ids and codes only
dc exec postgres psql -U tailorly       # SQL (read-only habits: BEGIN; … ROLLBACK;)
dc restart app                          # safe: jobs are idempotent; in-flight jobs are retried
```
The Hangfire dashboard is at `https://<domain>/admin/jobs` (Admin, two-step sign-in). Every POST there is audited.

## 3. When an alert arrives

Alerts come by email from the app (`AdminAlerts`, every 5 minutes; the same alert repeats every 6 hours while it lasts).
They hold counts, ids and error codes only.

| Alert | Meaning | Do |
|---|---|---|
| **Source X failed N times in a row** | The adapter failed 3+ runs; others keep running. | Admin → Status: `lastError`. `HTTP_429/403`: wait (circuit opens itself). `HTTP_404/410` or a schema error: the API changed; check the provider's docs and the fixture, fix the adapter. If the terms changed: disable the source. |
| **N scheduled digest run(s) failed** | Runs ended `failed` after their retries. | Job dashboard → Failed: error codes. `AI_UNAVAILABLE`: see AI errors. `TIMEOUT`: runs waited too long (see §4). Users get a digest only if a retry succeeds inside the 4-hour window. |
| **N digest email(s) unsent for over 60 minutes** | Runs finished, emails not sent. | The SMTP provider: credentials, quota, status page. Hangfire retries the email jobs; requeue failed ones in the dashboard once fixed. |
| **AI error rate X% over 15 minutes** | Provider errors or invalid replies. | Anthropic status page; Admin → Status (by task). Invalid replies after a model change in AI config: switch the task back. Runs fall back to rule-based digests by themselves. |
| **Platform enrichment token ceiling reached** | `job_enrichment` used today's budget (IST day). | Usually a fetch spike. Runs rank un-enriched jobs by rules until midnight IST. Raise `Ai__PlatformDailyEnrichmentTokenCeiling` only if it is normal growth. |
| **Disk N% used** | Over 80 %. | `docker system df`, `docker image prune -a`, `docker builder prune`; `du -sh /var/lib/docker/volumes/*`; check `/var/log`. Expired jobs are deleted daily. If documents grow: resize the disk (most clouds online), then `resize2fs`. |
| **Nightly backup failed / no backup recorded / no successful backup** | `backup.sh` failed, did not run, or never ran. | `tail -100 /var/log/tailorly-backup.log` shows the failed step. Run `deploy/backup.sh` by hand. Common: the rclone token expired, the bucket is full, the disk is full, `age` missing. |
| **TLS certificate expires in N days / could not be checked** | Caddy has not renewed, or is down. | `dc logs caddy`. Ports 80/443 must be reachable from the internet for ACME; the DNS record must point at the VM. `dc restart caddy`. |

## 4. Capacity and tuning (from the M6 load test)

`tests/load-reports/` holds the measured run. The digest slot is bound by the AI API's pace: all model calls go through
one gate of `Ai__MaxConcurrentCalls` (4) and runs through `Hangfire:WorkerCount` (4) workers.

- More users on one slot → longer until the last email. At 2 s per model call, 100 users on the same slot take about
  as long as the report shows. Within the 4-hour catch-up window this is fine; the **30-minute stuck-work sweep** is
  the real ceiling: a claimed run that has not started within 30 minutes is failed with `TIMEOUT`.
- First lever: `Ai__MaxConcurrentCalls` (only as far as the Anthropic rate-limit tier allows) together with
  `Hangfire__WorkerCount` (runs wait for a worker before they reach the AI gate). Raise both, e.g. to 8.
- Second lever: spread users (most keep the 18:00 default): change the default slot for new users, or start runs
  earlier and send at the slot time (SDD §16.2).
- Memory: the app is limited to 1.25 GB in `docker-compose.prod.yml`; Postgres to 768 MB.

## 5. Security operations
- **Admins**: only people who need it; each with two-step sign-in. Remove the role with SQL
  (`DELETE FROM asp_net_user_roles …`) and rotate their sessions with `UPDATE asp_net_users SET security_stamp = gen_random_uuid()::text WHERE id = …`.
- **Suspend** an abusive account in Admin → Users (ids and counts only; search by exact email). Suspended accounts
  cannot sign in, every call answers `ACCOUNT_DISABLED`, and no digest is sent.
- **Audit log**: every admin call and job-dashboard POST, sign-ins, two-step changes and deletions are in `audit_log`
  (12 months). `SELECT at, action, entity, entity_id, meta FROM audit_log WHERE actor_user_id = '…' ORDER BY at DESC;`
- **Logs** never contain résumé or job text, prompts, replies, emails or tokens. If you ever see one, treat it as an
  incident: rotate the affected token, fix the code path, and purge the log file (`docker compose logs` files under
  `/var/lib/docker/containers/*/`).
- **Secrets** live only in `deploy/.env` (mode 600) and the password manager. A leaked key: rotate at the provider,
  update `.env`, `dc up -d app`.
- **Account deletion** removes rows and files at once (hourly purge as a backstop) and appends to the deletion log,
  which is shipped every 15 minutes. Backups roll off after 30 days, so a deleted user's data is gone from every copy
  within 30 days; a restore replays the log (restore runbook).
