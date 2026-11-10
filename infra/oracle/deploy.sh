#!/usr/bin/env bash
# vybe-deploy: the only command that the deploy key can run on the VM (ADR 0009, docs/runbooks/oracle-vm.md).
# bootstrap.sh installs it as /opt/vybe/bin/vybe-deploy (root:root, 0755) and puts it in the deploy key's
# authorized_keys line as a forced command (restrict,command="..."). sshd then gives the requested command in
# SSH_ORIGINAL_COMMAND; this script checks every word, then runs itself again as root through one sudoers rule.
#
#   status                                  file hashes, host name, current and previous images (no secrets)
#   deploy <git-sha> <api-image> <web-image>
#                                           images: ghcr.io/seerj30/vybe-api@sha256:<64 hex>, .../vybe-web@sha256:...
#   rollback                                the previous release again (image-only; the database stays)
#
# A failed deploy starts the current release again. Output goes to the public workflow log: no values from
# /etc/vybe, no container logs (read them on the VM: docker compose -p vybe logs).
set -euo pipefail
umask 022

readonly ROOT=/opt/vybe
readonly COMPOSE_FILE="$ROOT/compose.yml"
readonly CURRENT="$ROOT/release.env"
readonly PREVIOUS="$ROOT/release.previous.env"
readonly ENV_DIR=/etc/vybe
readonly SELF="$ROOT/bin/vybe-deploy"
readonly API_REPO=ghcr.io/seerj30/vybe-api
readonly WEB_REPO=ghcr.io/seerj30/vybe-web
readonly DIGEST='@sha256:[0-9a-f]{64}'

fail() { echo "vybe-deploy: $*" >&2; exit 1; }

# Arguments: from sshd (forced command) or from the command line (an admin on the VM).
if [ "$#" -eq 0 ] && [ -n "${SSH_ORIGINAL_COMMAND:-}" ]; then
  case "$SSH_ORIGINAL_COMMAND" in *[!a-z0-9@:./\ -]*) fail "unexpected characters in the command" ;; esac
  read -r -a args <<< "$SSH_ORIGINAL_COMMAND"
  set -- "${args[@]}"
fi

check_args() {
  case "${1:-}" in
    status | rollback) [ "$#" -eq 1 ] || fail "usage: $1 (no arguments)" ;;
    deploy)
      [ "$#" -eq 4 ] || fail "usage: deploy <git-sha> <api-image> <web-image>"
      [[ "$2" =~ ^[0-9a-f]{40}$ ]] || fail "the git SHA must be 40 lowercase hex characters"
      [[ "$3" =~ ^${API_REPO//./\\.}${DIGEST}$ ]] || fail "the API image must be ${API_REPO}@sha256:<digest>"
      [[ "$4" =~ ^${WEB_REPO//./\\.}${DIGEST}$ ]] || fail "the web image must be ${WEB_REPO}@sha256:<digest>"
      ;;
    *) fail "usage: status | deploy <git-sha> <api-image> <web-image> | rollback" ;;
  esac
}
check_args "$@"

# The deploy user has no docker group: one sudoers rule allows this script, which checks its arguments again as root.
if [ "$(id -u)" -ne 0 ]; then
  exec sudo -n "$SELF" "$@"
fi

compose() { docker compose -p vybe -f "$COMPOSE_FILE" --env-file "$1" "${@:2}"; }

check_env_files() {
  local file
  for file in "$ENV_DIR/api.env" "$ENV_DIR/web.env"; do
    [ -f "$file" ] || fail "$file is missing: run stage-env.sh first (runbook, \"Stage the settings\")"
    [ "$(stat -c '%U:%G %a' "$file")" = "root:root 600" ] || fail "$file must be root:root with mode 600"
  done
}

image_of() { sed -n "s/^$2=//p" "$1" 2>/dev/null || true; }

status() {
  local file
  for file in "$COMPOSE_FILE" "$ROOT/Caddyfile" "$SELF"; do
    printf 'sha256 %s %s\n' "$(basename "$file")" "$(sha256sum "$file" | cut -d' ' -f1)"
  done
  # The public host name is not a secret (it is the site address).
  printf 'host %s\n' "$(sed -n 's/^VYBE_PUBLIC_HOST=//p' "$ENV_DIR/web.env" 2>/dev/null | tr -d "'\"")"
  printf 'current %s %s\n' "$(image_of "$CURRENT" VYBE_API_IMAGE)" "$(image_of "$CURRENT" VYBE_WEB_IMAGE)"
  printf 'previous %s %s\n' "$(image_of "$PREVIOUS" VYBE_API_IMAGE)" "$(image_of "$PREVIOUS" VYBE_WEB_IMAGE)"
}

# start <release file>: pull, then replace the containers; waits for both health checks.
start() {
  compose "$1" pull --quiet
  compose "$1" up -d --remove-orphans --wait --wait-timeout 240
}

deploy_release() {
  local next="$1"
  if start "$next"; then
    if [ -f "$CURRENT" ] && ! cmp -s "$CURRENT" "$next"; then mv "$CURRENT" "$PREVIOUS"; fi
    mv "$next" "$CURRENT"
    compose "$CURRENT" ps --format 'table {{.Service}}\t{{.Status}}'
    # Images older than 30 days that no container uses. Rollback images can be pulled again from GHCR.
    docker image prune --all --force --filter 'until=720h' >/dev/null
    echo "vybe-deploy: $(image_of "$CURRENT" VYBE_RELEASE) is running"
    return 0
  fi
  echo "vybe-deploy: the new release did not become healthy" >&2
  compose "$next" ps --format 'table {{.Service}}\t{{.Status}}' >&2 || true
  rm -f "$next"
  if [ -f "$CURRENT" ]; then
    echo "vybe-deploy: starting the current release again" >&2
    start "$CURRENT" >&2 || echo "vybe-deploy: the current release did not become healthy either" >&2
  fi
  exit 1
}

exec 9>/run/vybe-deploy.lock
flock -w 600 9 || fail "another deploy is running"

case "$1" in
  status) status ;;
  deploy)
    check_env_files
    next="$ROOT/release.next.env"
    printf 'VYBE_RELEASE=%s\nVYBE_API_IMAGE=%s\nVYBE_WEB_IMAGE=%s\n' "$2" "$3" "$4" > "$next"
    deploy_release "$next"
    ;;
  rollback)
    check_env_files
    [ -f "$PREVIOUS" ] || fail "no previous release on this VM: deploy the old digests instead"
    next="$ROOT/release.next.env"
    cp "$PREVIOUS" "$next"
    deploy_release "$next"
    ;;
esac
