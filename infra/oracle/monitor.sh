#!/usr/bin/env bash
# vybe-monitor: a free heartbeat for the VM (ADR 0009, docs/runbooks/oracle-vm.md "Monitoring").
# bootstrap.sh installs it as /opt/vybe/bin/vybe-monitor and runs it every 5 minutes (systemd timer vybe-monitor).
# It checks the root disk, the memory and the health of both containers, writes problems to the journal, and pings a
# heartbeat URL (for example a free healthchecks.io check) from /etc/vybe/monitor.env:
#   VYBE_MONITOR_PING_URL='https://hc-ping.com/<uuid>'
# A problem pings <url>/fail; a stopped VM sends no ping. Either way the heartbeat service sends an email.
set -uo pipefail

readonly DISK_LIMIT=85   # percent used on /
readonly MEM_LIMIT=90    # percent of memory in use (without page cache)
problems=()

disk=$(df --output=pcent / | tail -n 1 | tr -dc '0-9')
[ "${disk:-100}" -lt "$DISK_LIMIT" ] || problems+=("disk ${disk}% used")

mem=$(awk '/^MemTotal:/ {t=$2} /^MemAvailable:/ {a=$2} END {printf "%d", (t-a)*100/t}' /proc/meminfo)
[ "${mem:-100}" -lt "$MEM_LIMIT" ] || problems+=("memory ${mem}% used")

for container in vybe-api-1 vybe-web-1; do
  health=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' \
    "$container" 2>/dev/null || echo missing)
  [ "$health" = healthy ] || problems+=("$container $health")
done

message="ok"
if [ "${#problems[@]}" -gt 0 ]; then
  message=$(IFS=';'; echo "${problems[*]}")
  logger -p user.warning -t vybe-monitor "$message"
fi

url=$(sed -n "s/^VYBE_MONITOR_PING_URL='\(https:\/\/[A-Za-z0-9./_-]*\)'$/\1/p" /etc/vybe/monitor.env 2>/dev/null || true)
if [ -n "$url" ]; then
  [ "$message" = ok ] || url="$url/fail"
  # The URL goes through stdin (curl -K -), not the process arguments.
  printf 'url = "%s"\n' "$url" | curl -fsS --max-time 10 --retry 3 -o /dev/null --data-raw "$message" -K - \
    || logger -p user.warning -t vybe-monitor "the heartbeat ping failed"
fi

[ "$message" = ok ]
