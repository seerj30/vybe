#!/usr/bin/env bash
# Weekly encrypted logical dump of schema vybe (Supabase Free has no automatic backups). Runbook:
# docs/runbooks/backup-restore.md, "Weekly dump on Supabase Free". Run by .github/workflows/backup.yml.
#
#   1. pg_dump (custom format) as the read-only role vybe_backup (infra/oracle/backup-grants.sql), never the owner
#      role: this workflow has no reviewer. Through the session pooler (port 5432; never the transaction pooler), TLS
#      verify-full. The password goes to a pgpass file (mode 600) in a private directory: never in arguments or a URL.
#   2. age encrypts the stream to the public keys in infra/oracle/backup-recipients.txt. No plain dump touches the disk.
#      Only the owner has the private key.
#   3. The file goes to s3://vybe-exports/backups/vybe-<UTC time>.dump.age (Supabase Storage S3). The API's orphan sweep
#      reads only exports/ (S3ExportStorage, test OpsExportStorageTests.List_NeverReturnsBackups).
#   4. Retention: delete dumps older than RETENTION_DAYS, but always keep the newest MIN_KEEP.
#
# Environment: VYBE_BACKUP_DB_PASSWORD (vybe_backup), AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY (a Supabase S3 key).
# BACKUP_* overrides exist for a local test against PostgreSQL and MinIO only.
set -euo pipefail

readonly REF="pkrudxjeihxrkkdlfdmr"
readonly DB_HOST="${BACKUP_DB_HOST:-aws-0-eu-central-1.pooler.supabase.com}"
readonly DB_PORT="${BACKUP_DB_PORT:-5432}"
readonly DB_USER="${BACKUP_DB_USER:-vybe_backup.$REF}"
readonly DB_NAME="${BACKUP_DB_NAME:-postgres}"
# verify-full: the pooler's certificate must chain to the Supabase root CA in supabase-ca.crt.
readonly DB_SSLMODE="${BACKUP_DB_SSLMODE:-verify-full}"
readonly DOCKER_NETWORK="${BACKUP_DOCKER_NETWORK:-bridge}"
readonly S3_ENDPOINT="${BACKUP_S3_ENDPOINT:-https://$REF.storage.supabase.co/storage/v1/s3}"
readonly BUCKET="${BACKUP_BUCKET:-vybe-exports}"
readonly PREFIX="backups/"
readonly RETENTION_DAYS=35
readonly MIN_KEEP=4
# pg_dump must have the server's major version or newer. Pinned by digest (update: docker buildx imagetools inspect).
readonly PG_IMAGE="postgres:17.6-alpine@sha256:ef257d85f76e48da1c64832459b59fcaba1a4dac97bf5d7450c77753542eee94"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly HERE RECIPIENTS="$HERE/backup-recipients.txt" CA_FILE="$HERE/supabase-ca.crt"

fail() { echo "backup: $*" >&2; exit 1; }

[ -n "${VYBE_BACKUP_DB_PASSWORD:-}" ] || fail "VYBE_BACKUP_DB_PASSWORD is not set"
if [ -z "${AWS_ACCESS_KEY_ID:-}" ] || [ -z "${AWS_SECRET_ACCESS_KEY:-}" ]; then
  fail "the S3 key (AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY) is not set"
fi
grep -q '^age1[a-z0-9]\{58\}$' "$RECIPIENTS" || fail "$RECIPIENTS has no age public key (runbook: create the backup key)"
command -v age >/dev/null || fail "age is not installed"
tls_mount=()
if [ "$DB_SSLMODE" = verify-full ]; then
  grep -q 'BEGIN CERTIFICATE' "$CA_FILE" 2>/dev/null \
    || fail "$CA_FILE is missing: add the Supabase root CA (runbook backup-restore.md, \"Weekly dump on Supabase Free\")"
  tls_mount=(-v "$CA_FILE:/run/pg/ca.crt:ro" -e PGSSLROOTCERT=/run/pg/ca.crt)
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
chmod 700 "$work"

# pgpass fields escape ':' and '\'.
password=$(printf '%s' "$VYBE_BACKUP_DB_PASSWORD" | sed 's/\\/\\\\/g; s/:/\\:/g')
(umask 077 && printf '%s:%s:%s:%s:%s\n' "$DB_HOST" "$DB_PORT" "$DB_NAME" "$DB_USER" "$password" > "$work/pgpass")
unset password
(umask 077 && printf '[vybe-backup]\nhost=%s\nport=%s\ndbname=%s\nuser=%s\nsslmode=%s\n' \
  "$DB_HOST" "$DB_PORT" "$DB_NAME" "$DB_USER" "$DB_SSLMODE" > "$work/pg_service.conf")

# S3 client settings: Supabase Storage needs path-style addressing.
printf '[default]\nregion = eu-central-1\ns3 =\n  addressing_style = path\n' > "$work/aws-config"
export AWS_CONFIG_FILE="$work/aws-config" AWS_SHARED_CREDENTIALS_FILE=/dev/null AWS_ENDPOINT_URL_S3="$S3_ENDPOINT"

name="vybe-$(date -u +%Y%m%dT%H%M%SZ).dump.age"
echo "backup: dumping schema vybe to $name"
docker run --rm --network "$DOCKER_NETWORK" --read-only --tmpfs /tmp --cap-drop ALL \
  --security-opt no-new-privileges:true --user "$(id -u):$(id -g)" \
  -v "$work/pgpass:/run/pg/pgpass:ro" -v "$work/pg_service.conf:/run/pg/pg_service.conf:ro" ${tls_mount[@]+"${tls_mount[@]}"} \
  -e PGPASSFILE=/run/pg/pgpass -e PGSERVICEFILE=/run/pg/pg_service.conf -e PGSERVICE=vybe-backup \
  "$PG_IMAGE" pg_dump --schema=vybe --format=custom --no-owner \
  | age --encrypt --recipients-file "$RECIPIENTS" --output "$work/$name"
size=$(stat -c %s "$work/$name" 2>/dev/null || stat -f %z "$work/$name")
# An empty schema dump is still several kilobytes; less means the dump is broken.
[ "$size" -gt 2048 ] || fail "the encrypted dump has only $size bytes"

aws s3 cp --no-progress "$work/$name" "s3://$BUCKET/$PREFIX$name"
echo "backup: uploaded $PREFIX$name ($size bytes)"

# Retention. Only names that this script writes; newest first.
cutoff=$(date -u -d "-$RETENTION_DAYS days" +%Y%m%dT%H%M%SZ 2>/dev/null || date -u -v-"$RETENTION_DAYS"d +%Y%m%dT%H%M%SZ)
keys=$(aws s3api list-objects-v2 --bucket "$BUCKET" --prefix "${PREFIX}vybe-" --query 'Contents[].Key' --output text \
  | tr '\t' '\n' | grep -E "^${PREFIX}vybe-[0-9]{8}T[0-9]{6}Z\.dump\.age$" | sort -r || true)
grep -qxF "$PREFIX$name" <<< "$keys" || fail "the new dump is not in the bucket listing"
kept=0
while IFS= read -r key; do
  [ -n "$key" ] || continue
  kept=$((kept + 1))
  stamp="${key#"${PREFIX}vybe-"}"
  stamp="${stamp%.dump.age}"
  if [ "$kept" -gt "$MIN_KEEP" ] && [[ "$stamp" < "$cutoff" ]]; then
    aws s3api delete-object --bucket "$BUCKET" --key "$key" >/dev/null
    echo "backup: deleted $key (older than $RETENTION_DAYS days)"
  fi
done <<< "$keys"
echo "backup: done"
