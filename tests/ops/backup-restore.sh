#!/usr/bin/env bash
# End-to-end check of the operations scripts against a running local stack (docker compose --profile dev up -d):
#   1. two users sign up (Mailpit verification), upload a résumé each;
#   2. deploy/backup.sh (age-encrypted, "remote" = a local rclone path);
#   3. the second user deletes their account (deletion log), deploy/sync-deletion-log.sh ships the log;
#   4. deploy/restore-test.sh restores the backup taken BEFORE the deletion into a scratch database: the second user is
#      back, so the replay must purge them again; row counts and documents must match.
# Runs inside a throwaway container with the Docker CLI, age and rclone, as on the VM:
#   MSYS_NO_PATHCONV=1 docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v "$PWD:/work" -w /work \
#     --network tailorly_internal docker:28-cli sh -c 'apk add -q bash age rclone curl jq zip coreutils && bash tests/ops/backup-restore.sh'
set -Eeuo pipefail

cd "$(dirname "$0")/../.."
OUT="$(mktemp -d)"
export BACKUP_DIR="$OUT/local" BACKUP_REMOTE="$OUT/remote" BACKUP_LOCAL_DAYS=3
age-keygen -o "$OUT/backup.key" 2>/dev/null
BACKUP_AGE_RECIPIENT="$(age-keygen -y "$OUT/backup.key")"
export BACKUP_AGE_RECIPIENT BACKUP_AGE_IDENTITY="$OUT/backup.key"

BASE="https://localhost"
CURL=(curl -fsS -k --connect-to localhost:443:caddy:443)
MAILPIT="http://mailpit:8025/api/v1"
step() { echo; echo "== $*"; }

docx() { # a minimal valid résumé
  local dir; dir="$(mktemp -d)"
  mkdir -p "$dir/_rels" "$dir/word"
  printf '%s' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>' > "$dir/[Content_Types].xml"
  printf '%s' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>' > "$dir/_rels/.rels"
  printf '%s' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Asha Rao</w:t></w:r></w:p><w:p><w:r><w:t>Backend engineer with 6 years of .NET and PostgreSQL.</w:t></w:r></w:p><w:p><w:r><w:t>Experience</w:t></w:r></w:p><w:p><w:r><w:t>Senior Engineer, Acme Payments, 2020 - 2026</w:t></w:r></w:p><w:p><w:r><w:t>Built payment APIs in C# on PostgreSQL.</w:t></w:r></w:p></w:body></w:document>' > "$dir/word/document.xml"
  (cd "$dir" && zip -qX -r "$1" '[Content_Types].xml' _rels word)
  rm -rf "$dir"
}

signup() { # $1 = email; prints nothing, leaves a signed-in cookie jar at $OUT/$1.jar
  local email="$1" jar="$OUT/$1.jar" consent xsrf link uid token id
  consent="$("${CURL[@]}" "$BASE/api/v1/meta" | jq -r .consentVersion)"
  "${CURL[@]}" -c "$jar" -b "$jar" "$BASE/api/v1/auth/csrf" >/dev/null
  xsrf="$(awk '$6 == "XSRF-TOKEN" { print $7 }' "$jar")"
  "${CURL[@]}" -c "$jar" -b "$jar" -H "X-XSRF-TOKEN: $xsrf" -H 'Content-Type: application/json' "$BASE/api/v1/auth/register" \
    --data "{\"email\":\"$email\",\"password\":\"correct horse battery staple\",\"displayName\":\"Ops Test\",\"timeZone\":\"Asia/Kolkata\",\"consentVersion\":\"$consent\"}" >/dev/null
  for _ in $(seq 1 30); do
    id="$(curl -fsS "$MAILPIT/search?query=to:$email%20subject:Verify" | jq -r '.messages[0].ID // empty')"
    [[ -n "$id" ]] && break; sleep 1
  done
  link="$(curl -fsS "$MAILPIT/message/$id" | jq -r .Text | tr -d '\r' | grep -oE 'https?://[^ ]*/verify-email[^ ]*' | head -1)"
  uid="$(sed -E 's/.*[?&]userId=([^&]+).*/\1/' <<<"$link")"; token="$(sed -E 's/.*[?&]token=([^&]+).*/\1/' <<<"$link")"
  "${CURL[@]}" -c "$jar" -b "$jar" -H "X-XSRF-TOKEN: $xsrf" -H 'Content-Type: application/json' "$BASE/api/v1/auth/verify-email" \
    --data "{\"userId\":\"$uid\",\"token\":\"$token\"}" >/dev/null
  "${CURL[@]}" -c "$jar" -b "$jar" -H "X-XSRF-TOKEN: $xsrf" -H 'Content-Type: application/json' "$BASE/api/v1/auth/login" \
    --data "{\"email\":\"$email\",\"password\":\"correct horse battery staple\"}" >/dev/null
  "${CURL[@]}" -c "$jar" -b "$jar" "$BASE/api/v1/auth/csrf" >/dev/null # tokens are bound to the signed-in user
  echo "$uid" > "$OUT/$1.id"
}

call() { # $1 = email, rest = curl args
  local jar="$OUT/$1.jar" xsrf; shift
  xsrf="$(awk '$6 == "XSRF-TOKEN" { print $7 }' "$jar")"
  "${CURL[@]}" -c "$jar" -b "$jar" -H "X-XSRF-TOKEN: $xsrf" "$@"
}

step "users and résumés"
docx "$OUT/resume.docx"
A="keep-$RANDOM@example.test"; B="leave-$RANDOM@example.test"
for u in "$A" "$B"; do
  signup "$u"
  call "$u" -F "file=@$OUT/resume.docx;type=application/vnd.openxmlformats-officedocument.wordprocessingml.document" \
    "$BASE/api/v1/resumes" >/dev/null
done
sleep 5 # the parse job runs in the app's Hangfire server
call "$A" "$BASE/api/v1/resumes" | jq -r '"user A résumés: " + (length | tostring) + ", parse: " + (.[0].parseStatus // "?")'
B_ID="$(cat "$OUT/$B.id")"

step "backup"
bash deploy/backup.sh
STAMP="$(ls "$BACKUP_REMOTE" | grep -E '^20' | sort | tail -1)"
docker compose -f deploy/docker-compose.yml run --rm --no-deps -T --entrypoint cat app /data/ops/backup-status.json; echo

step "user B deletes the account after the backup"
call "$B" -X DELETE "$BASE/api/v1/account" >/dev/null
for _ in $(seq 1 30); do
  id="$(curl -fsS "$MAILPIT/search?query=to:$B%20subject:delet" | jq -r '.messages[0].ID // empty')"
  [[ -n "$id" ]] && break; sleep 1
done
link="$(curl -fsS "$MAILPIT/message/$id" | jq -r .Text | tr -d '\r' | grep -oE 'https?://[^ ]*confirm[^ ]*' | head -1)"
token="$(sed -E 's/.*[?&]token=([^&]+).*/\1/' <<<"$link")"
"${CURL[@]}" -H 'Content-Type: application/json' -H "X-XSRF-TOKEN: $(awk '$6 == "XSRF-TOKEN" { print $7 }' "$OUT/$B.jar")" \
  -b "$OUT/$B.jar" "$BASE/api/v1/account/confirm-deletion" --data "{\"userId\":\"$B_ID\",\"token\":\"$token\"}" >/dev/null
sleep 5
echo "user B rows in production: $(docker compose -f deploy/docker-compose.yml exec -T postgres psql -U tailorly -d tailorly -At -c "SELECT count(*) FROM asp_net_users WHERE id = '$B_ID'")"
bash deploy/sync-deletion-log.sh
grep -c "$B_ID" "$BACKUP_REMOTE/deletion-log/deletions.log" | sed 's/^/user B in the shipped deletion log: /'

step "restore test of the backup taken before the deletion"
bash deploy/restore-test.sh "$STAMP"

rm -rf "$OUT"
