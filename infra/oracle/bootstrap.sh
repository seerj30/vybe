#!/usr/bin/env bash
# One-time (and repeatable) set-up of the VYBE production VM: Oracle Cloud Always Free, VM.Standard.A1.Flex,
# Ubuntu 24.04 aarch64 (ADR 0009). Runbook: docs/runbooks/oracle-vm.md, "Bootstrap the VM".
#
# From the owner's Mac (the files of this folder at a reviewed commit of master):
#   scp -r infra/oracle ubuntu@<public ip>:vybe-oracle
#   scp ~/.ssh/vybe-deploy.pub ubuntu@<public ip>:vybe-oracle/
#   ssh ubuntu@<public ip> sudo bash vybe-oracle/bootstrap.sh --deploy-key vybe-oracle/vybe-deploy.pub
#   ssh ubuntu@<public ip> sudo reboot
#
#   --deploy-key <file>  the public key of the GitHub deploy key (ssh-ed25519). Needed the first time.
#   --files-only         only install the server files again (compose.yml, Caddyfile, vybe-deploy, vybe-monitor),
#                        for example after a merged change in infra/oracle. The deploy workflow refuses to deploy
#                        while these files differ from the commit it deploys.
#
# What it does (each step is safe to run again): OS updates and unattended security upgrades, Docker Engine and the
# compose plugin from Docker's apt repository (key checked by fingerprint), Docker log rotation, a deploy user that
# can run only /opt/vybe/bin/vybe-deploy, SSH hardening (keys only, no root), ufw (22, 80, 443 only), fail2ban,
# a swap file, journald limits, time sync, the server files and the vybe-monitor timer.
# The OCI security list (or NSG) must also allow 80 and 443: this script cannot change it.
set -euo pipefail

readonly DEPLOY_USER=vybe-deploy
readonly ROOT=/opt/vybe
readonly CADDY_DATA=/var/lib/vybe/caddy
readonly CADDY_UID=10001              # the user of the web image (infra/web/Dockerfile)
# Docker's release signing key (https://docs.docker.com/engine/install/ubuntu/).
readonly DOCKER_KEY_FINGERPRINT=9DC858229FC7DD38854AE2D88D81803C0EBFCD88
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SRC

say() { printf '\n== %s\n' "$*"; }
fail() { echo "bootstrap: $*" >&2; exit 1; }

deploy_key=""
files_only=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --deploy-key) deploy_key="${2:-}"; shift 2 ;;
    --files-only) files_only=true; shift ;;
    *) fail "usage: bootstrap.sh [--deploy-key <file>] [--files-only]" ;;
  esac
done

[ "$(id -u)" -eq 0 ] || fail "run it with sudo"
# shellcheck source=/dev/null
. /etc/os-release
if [ "${ID:-}" != ubuntu ] || [ "${VERSION_ID:-}" != "24.04" ]; then fail "this script is for Ubuntu 24.04"; fi
[ "$(uname -m)" = aarch64 ] || echo "bootstrap: warning: not aarch64 ($(uname -m)); the images are built for linux/arm64" >&2
admin="${SUDO_USER:-}"

# write_if_changed <file> <mode>: stdin to <file> when the content differs. Exit code 0 = changed.
write_if_changed() {
  local tmp
  tmp=$(mktemp)
  cat > "$tmp"
  if [ -f "$1" ] && cmp -s "$tmp" "$1"; then rm -f "$tmp"; return 1; fi
  install -o root -g root -m "$2" "$tmp" "$1"
  rm -f "$tmp"
}

install_files() {
  say "Server files in $ROOT"
  local file
  for file in compose.yml Caddyfile deploy.sh monitor.sh; do
    [ -f "$SRC/$file" ] || fail "$SRC/$file is missing: copy the whole infra/oracle folder"
  done
  install -d -o root -g root -m 0755 "$ROOT" "$ROOT/bin"
  install -d -o root -g root -m 0700 /etc/vybe
  install -o root -g root -m 0644 "$SRC/compose.yml" "$ROOT/compose.yml"
  install -o root -g root -m 0644 "$SRC/Caddyfile" "$ROOT/Caddyfile"
  install -o root -g root -m 0755 "$SRC/deploy.sh" "$ROOT/bin/vybe-deploy"
  install -o root -g root -m 0755 "$SRC/monitor.sh" "$ROOT/bin/vybe-monitor"
  install -d -o root -g root -m 0755 "$(dirname "$CADDY_DATA")"
  install -d -o "$CADDY_UID" -g "$CADDY_UID" -m 0700 "$CADDY_DATA"
  sha256sum "$ROOT/compose.yml" "$ROOT/Caddyfile" "$ROOT/bin/vybe-deploy"
}

if [ "$files_only" = true ]; then
  install_files
  exit 0
fi

export DEBIAN_FRONTEND=noninteractive

say "OS updates"
apt-get update -q
apt-get -y -q -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold full-upgrade
apt-get -y -q install ca-certificates curl gnupg ufw fail2ban python3-systemd unattended-upgrades

say "Unattended security upgrades (reboot at 04:30 UTC when a kernel or library needs it)"
write_if_changed /etc/apt/apt.conf.d/20auto-upgrades 0644 <<'EOF' || true
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF
write_if_changed /etc/apt/apt.conf.d/52vybe-unattended-upgrades 0644 <<'EOF' || true
// VYBE (infra/oracle/bootstrap.sh): security updates only (the Ubuntu default origins). Docker updates stay manual
// (runbook "OS and Docker updates"). The containers restart after a reboot (restart: unless-stopped).
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "04:30";
Unattended-Upgrade::Remove-Unused-Kernel-Packages "true";
Unattended-Upgrade::Remove-Unused-Dependencies "true";
EOF
systemctl enable --now unattended-upgrades

say "Docker Engine and the compose plugin (Docker's apt repository)"
install -d -m 0755 /etc/apt/keyrings
key=$(mktemp)
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o "$key"
fingerprint=$(gpg --show-keys --with-colons "$key" 2>/dev/null | awk -F: '/^fpr/ {print $10; exit}')
[ "$fingerprint" = "$DOCKER_KEY_FINGERPRINT" ] || { rm -f "$key"; fail "Docker's apt key has fingerprint '$fingerprint', expected $DOCKER_KEY_FINGERPRINT"; }
install -o root -g root -m 0644 "$key" /etc/apt/keyrings/docker.asc
rm -f "$key"
write_if_changed /etc/apt/sources.list.d/docker.sources 0644 <<EOF || true
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: ${VERSION_CODENAME}
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
EOF
apt-get update -q
apt-get -y -q install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
install -d -m 0755 /etc/docker
# Log rotation for every container; no userland proxy, so Caddy sees the real client address (published ports use
# DNAT); containers keep running while dockerd restarts (updates); no privilege gain in any container.
if write_if_changed /etc/docker/daemon.json 0644 <<'EOF'
{
  "log-driver": "local",
  "log-opts": { "max-size": "20m", "max-file": "5" },
  "userland-proxy": false,
  "live-restore": true,
  "no-new-privileges": true
}
EOF
then
  systemctl restart docker
fi
systemctl enable --now docker

say "Swap file (2 GB, only if the VM has no swap)"
if [ -z "$(swapon --show --noheadings)" ]; then
  [ -f /swapfile ] || { fallocate -l 2G /swapfile; chmod 600 /swapfile; mkswap /swapfile >/dev/null; }
  swapon /swapfile
  grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi
if write_if_changed /etc/sysctl.d/60-vybe.conf 0644 <<'EOF'
vm.swappiness = 10
EOF
then
  sysctl -q --system
fi

say "journald limits"
install -d -m 0755 /etc/systemd/journald.conf.d
if write_if_changed /etc/systemd/journald.conf.d/60-vybe.conf 0644 <<'EOF'
[Journal]
SystemMaxUse=500M
MaxRetentionSec=1month
EOF
then
  systemctl restart systemd-journald
fi

say "Time sync"
if systemctl is-active --quiet chrony; then
  echo "chrony is active: kept as it is"
else
  install -d -m 0755 /etc/systemd/timesyncd.conf.d
  # The OCI time server (link-local), then Ubuntu's pool.
  if write_if_changed /etc/systemd/timesyncd.conf.d/60-vybe.conf 0644 <<'EOF'
[Time]
NTP=169.254.169.254
FallbackNTP=ntp.ubuntu.com
EOF
  then
    systemctl restart systemd-timesyncd
  fi
  timedatectl set-ntp true
fi

say "Deploy user $DEPLOY_USER (runs only $ROOT/bin/vybe-deploy)"
# The home directory and .ssh belong to root, and the shell is /bin/sh (dash reads no rc file for "sh -c"): the user
# cannot change its own SSH keys or start-up files, even if it could ever write a file.
readonly DEPLOY_HOME=/var/lib/vybe-deploy
install -d -o root -g root -m 0755 "$DEPLOY_HOME"
if ! id "$DEPLOY_USER" >/dev/null 2>&1; then
  useradd --home-dir "$DEPLOY_HOME" --no-create-home --shell /bin/sh --comment "VYBE deploy (GitHub Actions)" "$DEPLOY_USER"
fi
usermod --home "$DEPLOY_HOME" --shell /bin/sh "$DEPLOY_USER"
passwd -l "$DEPLOY_USER" >/dev/null
# Not in the docker group (that is root without a password): vybe-deploy runs as root through this one rule and
# checks its arguments.
write_if_changed /etc/sudoers.d/vybe-deploy 0440 <<EOF || true
$DEPLOY_USER ALL=(root) NOPASSWD: $ROOT/bin/vybe-deploy
EOF
visudo -cqf /etc/sudoers.d/vybe-deploy || fail "/etc/sudoers.d/vybe-deploy is not valid"
home="$DEPLOY_HOME"
if [ -n "$deploy_key" ]; then
  [ -f "$deploy_key" ] || fail "$deploy_key does not exist"
  [ "$(grep -c . "$deploy_key")" -eq 1 ] || fail "$deploy_key must have exactly one line"
  line=$(grep . "$deploy_key")
  [[ "$line" =~ ^ssh-ed25519\ [A-Za-z0-9+/=]+(\ [A-Za-z0-9@._-]+)?$ ]] || fail "$deploy_key must be one ssh-ed25519 public key"
  install -d -o root -g root -m 0755 "$home/.ssh"
  # restrict: no shell, no pty, no forwarding. The key can only run vybe-deploy (SSH_ORIGINAL_COMMAND is checked).
  printf 'restrict,command="%s/bin/vybe-deploy" %s\n' "$ROOT" "$line" \
    | write_if_changed "$home/.ssh/authorized_keys" 0644 || true
elif [ ! -s "$home/.ssh/authorized_keys" ]; then
  fail "the first run needs --deploy-key <file>"
fi

say "SSH: keys only, no root login"
if [ -n "$admin" ] && [ "$admin" != root ]; then
  admin_home=$(getent passwd "$admin" | cut -d: -f6)
  [ -s "$admin_home/.ssh/authorized_keys" ] || fail "$admin has no SSH key: stopped before SSH changes (lock-out risk)"
  allow="AllowUsers $admin $DEPLOY_USER"
else
  allow="# AllowUsers not set: run this script with sudo from the admin user to set it"
fi
# 00-: sshd uses the first value it reads, so this file wins over 50-cloud-init.conf.
write_if_changed /etc/ssh/sshd_config.d/00-vybe.conf 0644 <<EOF || true
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
PubkeyAuthentication yes
AuthenticationMethods publickey
MaxAuthTries 3
LoginGraceTime 30
X11Forwarding no
AllowAgentForwarding no
AllowTcpForwarding no
$allow
EOF
sshd -t || fail "sshd configuration test failed: fix /etc/ssh/sshd_config.d/00-vybe.conf before you log out"
systemctl reload ssh

say "Firewall: ufw allows 22, 80 and 443 only"
# Oracle's Ubuntu images block everything except 22 with iptables-persistent rules. ufw replaces them (after the
# reboot at the end; until then both rule sets allow SSH).
if systemctl is-enabled --quiet netfilter-persistent 2>/dev/null; then
  systemctl disable netfilter-persistent
  for rules in /etc/iptables/rules.v4 /etc/iptables/rules.v6; do
    if [ -f "$rules" ]; then mv "$rules" "$rules.oracle-disabled"; fi
  done
fi
ufw default deny incoming >/dev/null
ufw default allow outgoing >/dev/null
ufw limit 22/tcp comment ssh >/dev/null
ufw allow 80/tcp comment http >/dev/null
ufw allow 443/tcp comment https >/dev/null
ufw --force enable >/dev/null
ufw status verbose

say "Containers cannot reach the OCI metadata service (169.254.0.0/16)"
# A request forgery bug in the API must not read instance metadata. DOCKER-USER is Docker's chain for own rules; this
# unit adds the rule again after every Docker start. DNS and NTP of the host do not pass this chain.
write_if_changed /etc/systemd/system/vybe-docker-rules.service 0644 <<'EOF' || true
[Unit]
Description=VYBE: containers on the vybe network cannot reach link-local addresses (OCI metadata)
After=docker.service
Requires=docker.service
PartOf=docker.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/bin/sh -c 'iptables -C DOCKER-USER -s 172.30.80.0/24 -d 169.254.0.0/16 -j DROP 2>/dev/null || iptables -I DOCKER-USER -s 172.30.80.0/24 -d 169.254.0.0/16 -j DROP'

[Install]
WantedBy=docker.service
EOF
systemctl daemon-reload
systemctl enable vybe-docker-rules.service
systemctl restart vybe-docker-rules.service

say "fail2ban (SSH)"
if write_if_changed /etc/fail2ban/jail.d/60-vybe.local 0644 <<'EOF'
[sshd]
enabled = true
backend = systemd
maxretry = 5
findtime = 10m
bantime = 1h
EOF
then
  systemctl restart fail2ban
fi
systemctl enable --now fail2ban

install_files

say "vybe-monitor timer (every 5 minutes)"
write_if_changed /etc/systemd/system/vybe-monitor.service 0644 <<EOF || true
[Unit]
Description=VYBE heartbeat (disk, memory, container health)
After=docker.service

[Service]
Type=oneshot
ExecStart=$ROOT/bin/vybe-monitor
EOF
write_if_changed /etc/systemd/system/vybe-monitor.timer 0644 <<'EOF' || true
[Unit]
Description=VYBE heartbeat every 5 minutes

[Timer]
OnBootSec=5min
OnUnitActiveSec=5min

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now vybe-monitor.timer

say "Done"
echo "The host key for the GitHub variable VYBE_VM_HOST_KEY (put the public IP first: '<ip> ssh-ed25519 ...'):"
cut -d' ' -f1-2 /etc/ssh/ssh_host_ed25519_key.pub
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
echo "Next: reboot (sudo reboot), then run stage-env.sh. Open 80 and 443 in the OCI security list too."
