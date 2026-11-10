#!/usr/bin/env bash
# vybe-monitor: a free heartbeat for the VM (ADR 0009, docs/runbooks/oracle-vm.md "Monitoring").
# bootstrap.sh installs it as /opt/vybe/bin/vybe-monitor and runs it every 5 minutes (systemd timer vybe-monitor).
# It checks the root disk, the memory and the health of both containers, writes problems to the journal, and pings a
# heartbeat URL (for example a free healthchecks.io check) from /etc/vybe/monitor.env:
#   VYBE_MONITOR_PING_URL='https://hc-ping.com/<uuid>'
# A problem pings <url>/fail; a stopped VM sends no ping. Either way the heartbeat service sends an email.
#
# Docker does not restart an unhealthy container. This script restarts the API only when it is unhealthy in
# UNHEALTHY_RUNS runs in a row AND its liveness probe (/health/live, no database) also fails: a hung process. When only
# readiness fails (database outage, pending migration), a restart cannot help, so it only alerts. At most
# MAX_RESTARTS restarts in RESTART_WINDOW seconds, then it only alerts: a fault cannot cause a restart loop.
set -uo pipefail

readonly DISK_LIMIT=85   # percent used on /
readonly MEM_LIMIT=90    # percent of memory in use (without page cache)
readonly UNHEALTHY_RUNS=2
readonly MAX_RESTARTS=3
readonly RESTART_WINDOW=21600   # 6 hours
readonly STATE=/var/lib/vybe/monitor
problems=()
install -d -o root -g root -m 0700 "$STATE"

disk=$(df --output=pcent / | tail -n 1 | tr -dc '0-9')
[ "${disk:-100}" -lt "$DISK_LIMIT" ] || problems+=("disk ${disk}% used")

mem=$(awk '/^MemTotal:/ {t=$2} /^MemAvailable:/ {a=$2} END {printf "%d", (t-a)*100/t}' /proc/meminfo)
[ "${mem:-100}" -lt "$MEM_LIMIT" ] || problems+=("memory ${mem}% used")

for container in vybe-api-1 vybe-web-1; do
  health=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' \
    "$container" 2>/dev/null || echo missing)
  [ "$health" = healthy ] || problems+=("$container $health")
  if [ "$container" = vybe-api-1 ]; then api_health="$health"; fi
done

# API auto-restart, capped (see the header).
if [ "${api_health:-missing}" = unhealthy ]; then
  runs=$(( $(cat "$STATE/api-unhealthy-runs" 2>/dev/null || echo 0) + 1 ))
else
  runs=0
fi
echo "$runs" > "$STATE/api-unhealthy-runs"
if [ "$runs" -ge "$UNHEALTHY_RUNS" ]; then
  # Liveness from the web container (the API image has no shell or curl; the web image has busybox wget).
  if docker exec vybe-web-1 wget -q -T 5 -O /dev/null http://api:8080/health/live 2>/dev/null; then
    problems+=("api alive but not ready: database or migration, no restart")
  else
    now=$(date +%s)
    recent=$(awk -v since=$((now - RESTART_WINDOW)) '$1 >= since' "$STATE/api-restarts" 2>/dev/null | wc -l)
    if [ "$recent" -lt "$MAX_RESTARTS" ]; then
      echo "$now" >> "$STATE/api-restarts"
      if docker restart --time 35 vybe-api-1 >/dev/null; then
        problems+=("api hung: restarted ($((recent + 1)) of $MAX_RESTARTS in 6 h)")
      else
        problems+=("api hung: restart failed")
      fi
      echo 0 > "$STATE/api-unhealthy-runs"
    else
      problems+=("api hung: restart limit reached ($MAX_RESTARTS in 6 h), no restart")
    fi
  fi
fi

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
