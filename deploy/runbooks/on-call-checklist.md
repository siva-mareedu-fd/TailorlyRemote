# On-call checklist

One page for when something is wrong. Details: [operator runbook](operator-runbook.md) §3 (each alert),
[restore runbook](restore-runbook.md).

## Before you are on call
- [ ] You can SSH to the VM as `deploy` and run `docker compose … ps`.
- [ ] You can sign in to the admin console (two-step sign-in set up; recovery codes in the password manager).
- [ ] You have the password manager entries: `deploy/.env`, the age backup key, the registry token, provider logins
      (Anthropic, email provider, DNS, object storage, cloud console).
- [ ] Alert emails reach you (`Alerts__To`, or your Admin account's verified email). Test: Admin → Status loads, and the
      last alert in `audit_log` (`action = 'alert.sent'`) reached you.
- [ ] You know where status pages are: Anthropic, the email provider, the cloud provider.

## When paged
1. **Is the site up?** `curl -fsS https://<domain>/readyz` (database reachable) and open the SPA.
   - Down → `dc ps`; `dc logs --since 15m app caddy`; `dc up -d`. Disk full? (`df -h`) → operator runbook §3 "Disk".
2. **Read the alert subject** and follow its row in operator runbook §3. Alerts repeat every 6 hours while the
   condition lasts; a silence after a fix means it is gone.
3. **Admin → Status**: sources, digests (24 h: failed, unsent), AI errors by task, enrichment vs ceiling, disk, queue.
4. **Job dashboard** (`/admin/jobs`): Failed jobs and their error codes (messages are redacted by design; codes and
   ids are enough). Requeue only after the cause is fixed.
5. **Contain, then fix.** Fast, safe levers:
   - A source misbehaves → Admin → Sources → disable it (others keep running).
   - A model misbehaves → Admin → AI config → switch the task's model back, or disable the task (callers fail with
     `AI_UNAVAILABLE`, digests fall back to rules). Takes effect at once.
   - Digest emails must stop → Admin → Flags → `digest.email_enabled` off.
   - Signup abuse → Admin → Flags → `signup.require_invite_code` on; suspend accounts in Admin → Users.
   - A bad release → `deploy/deploy.sh --rollback`.
6. **Never** paste résumé text, job text, emails, tokens or keys into tickets, chat or AI tools. Use ids.

## Data incidents (privacy first)
- Suspected cross-user exposure, leaked logs or a lost key → treat as an incident immediately: contain (disable the
  feature or take the site down), preserve `audit_log` and logs, rotate secrets, and inform the owner. India's DPDP Act
  may require notifying affected users and the Data Protection Board.
- Never restore a backup without replaying the deletion log (restore runbook §3 step 8).

## After
- [ ] Write a short incident note: what, when (UTC/IST), impact (users, digests missed), cause, fix, follow-ups.
- [ ] If a digest slot was missed: users get the next day's digest automatically; "Run now" works for them meanwhile.
- [ ] Add a test or an alert so the same failure is caught earlier.
