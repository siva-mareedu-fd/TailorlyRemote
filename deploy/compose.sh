# shellcheck shell=bash
# Sourced by the deploy scripts. Loads deploy/.env and sets COMPOSE, the docker compose command for this VM.
#
# deploy/.env is a Compose env file, not a shell script (values such as "Tailorly Remote <no-reply@…>" are not
# quoted), so it is read line by line and never evaluated. Variables already in the environment win, as in Compose.
# TAILORLY_PROD=1 (set on the production VM) adds docker-compose.prod.yml: registry images and resource limits.

_deploy_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

load_env() {
  local file="$1" line key value
  [[ -f "$file" ]] || return 0
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    [[ "$line" =~ ^[[:space:]]*(#|$) || "$line" != *=* ]] && continue
    key="${line%%=*}"
    value="${line#*=}"
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    [[ -n "${!key+x}" ]] || export "$key=$value"
  done < "$file"
}

load_env "$_deploy_dir/.env"
COMPOSE=(docker compose -f "$_deploy_dir/docker-compose.yml")
if [[ "${TAILORLY_PROD:-0}" == "1" ]]; then COMPOSE+=(-f "$_deploy_dir/docker-compose.prod.yml"); fi
