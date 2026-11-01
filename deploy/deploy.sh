#!/usr/bin/env bash
# Deploy one image tag on the VM (SDD §13.3): pull → migrate (one-shot) → up -d → smoke test.
#
#   TAG=<git sha> deploy/deploy.sh            deploy that build (CI's deploy job runs this over SSH)
#   deploy/deploy.sh --rollback               re-deploy the previous tag (migrations are backward compatible)
#   SKIP_BACKUP=1 TAG=… deploy/deploy.sh      skip the pre-deploy backup (not recommended)
#
# Needs deploy/.env with TAILORLY_PROD=1, IMAGE_REPO, SITE_ADDRESS, POSTGRES_PASSWORD; optional SMOKE_EMAIL and
# SMOKE_PASSWORD for a scripted sign-in (a normal, verified, non-admin account kept for this purpose).
set -Eeuo pipefail

DEPLOY_DIR="$(cd "$(dirname "$0")" && pwd)"
HISTORY="$DEPLOY_DIR/.deployed-tags"
log() { echo "[deploy $(date -u +%H:%M:%S)] $*"; }
fail() { log "FAILED: $*"; exit 1; }

[[ -f "$DEPLOY_DIR/.env" ]] || fail "deploy/.env is missing (copy deploy/.env.example)"
# shellcheck source=deploy/compose.sh
. "$DEPLOY_DIR/compose.sh"   # loads deploy/.env and sets COMPOSE
[[ "${TAILORLY_PROD:-0}" == "1" ]] || fail "set TAILORLY_PROD=1 in deploy/.env on the production VM"
[[ -n "${IMAGE_REPO:-}" && -n "${POSTGRES_PASSWORD:-}" && -n "${SITE_ADDRESS:-}" ]] || fail "IMAGE_REPO, POSTGRES_PASSWORD and SITE_ADDRESS are required"

if [[ "${1:-}" == "--rollback" ]]; then
  [[ -f "$HISTORY" && $(wc -l < "$HISTORY") -ge 2 ]] || fail "no previous tag in $HISTORY"
  TAG="$(tail -n 2 "$HISTORY" | head -n 1 | cut -d' ' -f2)"
  log "rolling back to $TAG"
fi
[[ -n "${TAG:-}" ]] || fail "set TAG to the git SHA to deploy"
export TAG

log "pulling $IMAGE_REPO/tailorly-app:$TAG and tailorly-web:$TAG"
"${COMPOSE[@]}" pull migrate app caddy

if [[ "${SKIP_BACKUP:-0}" != "1" && -n "${BACKUP_AGE_RECIPIENT:-}" ]] && "${COMPOSE[@]}" ps --status running postgres | grep -q postgres; then
  log "pre-deploy backup"
  "$DEPLOY_DIR/backup.sh"
fi

# Migrations run first, as their own step, and must finish before the new app starts (SDD §12.5).
log "migrating"
"${COMPOSE[@]}" up -d --wait postgres
"${COMPOSE[@]}" run --rm migrate

log "starting"
"${COMPOSE[@]}" up -d --remove-orphans --wait app caddy

log "smoke test"
BASE="https://$SITE_ADDRESS"
CURL=(curl -fsS --max-time 10 --retry 10 --retry-delay 3 --retry-all-errors)
[[ "$SITE_ADDRESS" == "localhost" ]] && CURL+=(-k)
"${CURL[@]}" "$BASE/healthz" >/dev/null || fail "/healthz"
"${CURL[@]}" "$BASE/readyz" >/dev/null || fail "/readyz (database)"
"${CURL[@]}" "$BASE/api/v1/meta" | grep -q '"consentVersion"' || fail "/api/v1/meta"
"${CURL[@]}" "$BASE/" | grep -q '<div id="root">' || fail "SPA"

if [[ -n "${SMOKE_EMAIL:-}" && -n "${SMOKE_PASSWORD:-}" ]]; then
  JAR="$(mktemp)"
  trap 'rm -f "$JAR"' EXIT
  "${CURL[@]}" -c "$JAR" -b "$JAR" "$BASE/api/v1/auth/csrf" >/dev/null
  XSRF="$(awk '$6 == "XSRF-TOKEN" { print $7 }' "$JAR")"
  BODY="$(printf '{"email":"%s","password":"%s"}' "$SMOKE_EMAIL" "$SMOKE_PASSWORD")"
  # No retries here: repeated failed sign-ins would lock the smoke account.
  ONCE=(curl -fsS --max-time 10)
  [[ "$SITE_ADDRESS" == "localhost" ]] && ONCE+=(-k)
  "${ONCE[@]}" -c "$JAR" -b "$JAR" -H "Content-Type: application/json" -H "X-XSRF-TOKEN: $XSRF" \
    --data "$BODY" "$BASE/api/v1/auth/login" >/dev/null || fail "scripted sign-in"
  "${ONCE[@]}" -b "$JAR" "$BASE/api/v1/me" >/dev/null || fail "GET /me after sign-in"
  log "scripted sign-in ok"
else
  log "no SMOKE_EMAIL/SMOKE_PASSWORD: scripted sign-in skipped"
fi

echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) $TAG" >> "$HISTORY"
log "deployed $TAG"
