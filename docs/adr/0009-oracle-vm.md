# ADR 0009: Production on one Oracle Cloud Always Free VM

- Status: accepted (configuration only; nobody has deployed yet)
- Date: 2026-11-10
- Hosting: [decision 7](../decisions/07-hosting.md), changed on 2026-11-10 by the product owner: no paid hosting;
  option D (self-managed VM) on Oracle Cloud Always Free, Frankfurt. Supabase Free stays for PostgreSQL and Auth.
- Operations: [ADR 0006](0006-operations.md); the Fly.io design that this replaces for now: [ADR 0008](0008-fly-deployment.md)
- Procedure: [oracle-vm.md](../runbooks/oracle-vm.md); backups: [backup-restore.md](../runbooks/backup-restore.md)

## Context

The product owner does not want to pay for hosting. Oracle Cloud Always Free gives Ampere A1 capacity (4 OCPU,
24 GB in total) in the home region at no cost. One VM must now do what Fly.io did: end TLS, serve the web app,
proxy `/api` on the same origin, run the always-on API with its workers, and deploy reviewed images. Nobody operates
the VM full time, so the set-up must be scripted, hardened by default, and observable with free tools. Supabase
Free has no automatic backups.

The rules of ADR 0006 and ADR 0008 stay: one image per service, the owner credential only in the release step and
never on the server, forwarded headers only from a known network (never `/0`), the hosting headers of the web app,
logs without request paths, deploys of reviewed commits by digest.

## Decision

1. **One VM, Docker Compose, two containers.** `VM.Standard.A1.Flex` (2 OCPU, 12 GB, within the free limit),
   Ubuntu 24.04 or 26.04 LTS aarch64 (production: 26.04 since 2026-11-10), home region `eu-frankfurt-1` (Supabase is in `eu-central-1`). `infra/oracle/compose.yml`
   runs `api` (no published port) and `web` (the existing web image, ports 80 and 443). Both: read-only root,
   `tmpfs` at `/tmp`, all capabilities dropped, `no-new-privileges`, `init`, `restart: unless-stopped`, memory,
   CPU and PID limits (API 2 GB / 1.5 CPU; web 512 MB / 0.5 CPU), 35 s stop grace.
2. **Caddy ends TLS.** `infra/oracle/Caddyfile` is the Fly.io Caddyfile with automatic HTTPS for
   `VYBE_PUBLIC_HOST` (Let's Encrypt HTTP challenge on port 80) and without a trusted proxy: the client address is
   the TCP peer. Caddy runs as UID 10001 without capabilities; the container's own sysctl
   `net.ipv4.ip_unprivileged_port_start=0` allows ports 80 and 443. Certificates are in a host directory
   (`/var/lib/vybe/caddy`), because Let's Encrypt limits new certificates per week. HTTP/3 is off (no UDP port).
   compose mounts this file over the image's Caddyfile, so the web image stays the same for Fly.io and the VM.
3. **sslip.io for the first deploy, an own domain before real players.** The first host name is
   `<public IPv4 with dashes>.sslip.io`, so Let's Encrypt works without a domain. So that the name stays the same,
   a reserved public IP is mandatory. Before real players, an own domain replaces it (an `A` record and a new value
   in `stage-env.sh`): sslip.io is a third party that the team does not control. The host name is also the
   Supabase Site URL, the redirect URL and `Challenges__ShareBaseUrl`: they change together (runbook "Host name").
4. **Client address.** Docker publishes ports with DNAT and the userland proxy is off (`daemon.json`), so Caddy sees
   the real client address. Caddy replaces any client `X-Forwarded-For` with it. The API trusts forwarded headers
   only from the fixed compose subnet `172.30.80.0/24` (`ReverseProxy__KnownNetworks__0`). CI proves that 11 guest
   creations with 11 forged `X-Forwarded-For` values get 429 on the 11th.
5. **API health check without curl.** The chiseled image has no shell or curl. A new command
   `dotnet Vybe.Api.dll healthcheck` (`HealthProbe`) calls `/health/ready` on the container's own port and exits
   0 or 1, before any host or configuration starts. Docker marks the container unhealthy; `up --wait` stops a bad
   deploy. Docker does not restart an unhealthy container, so a database outage does not cause restart loops.
6. **Settings on the server, never in the repository.** `/etc/vybe/api.env` and `/etc/vybe/web.env`
   (`root:root`, mode 600) are written by `stage-env.sh`: values from no-echo prompts, through stdin and sudo, in
   single quotes (compose reads single-quoted values literally, so a `$` in a password stays). The invite secret is
   made on the VM and kept on later runs. The owner credential is never on the VM.
7. **Images for `linux/arm64` on GHCR, by digest, attested.** `deploy-oracle.yml` has three jobs, each with only
   its own permissions. `guard` (no secrets): by hand or `v*` tag only, the commit on `master` with green CI (as
   `deploy.yml`), rollback inputs (a rollback SHA must also be on `master`). `build` (no environment, no secrets;
   `packages: write`, `id-token: write`): builds both images on the free native arm64 runner
   (`ubuntu-24.04-arm`, no QEMU), pushes `ghcr.io/seerj30/vybe-api` and `vybe-web` by digest and signs a build
   provenance attestation for each (`actions/attest-build-provenance`). `deploy` (environment
   `production-oracle` with required reviewers; `packages: read`, no write): `gh attestation verify` refuses any
   image that this workflow did not build from the given SHA on a GitHub-hosted runner, then the release step
   (owner role from the environment secret, as ADR 0008 #6), and only then the SSH key is written, used and
   removed. The packages are public (no secrets in images), so the VM needs no registry credential. Actions are
   pinned by SHA; one deploy at a time.
8. **The deploy key can run one command.** The GitHub deploy key's `authorized_keys` line is
   `restrict,command="/opt/vybe/bin/vybe-deploy"`. `vybe-deploy` accepts only `status`, `rollback` and
   `deploy <git sha> <api image> <web image>` with strict patterns (GHCR repositories of this project, digests
   only), then runs itself as root through one sudoers rule (checked with `visudo` before it is installed). The deploy user is **not** in the `docker` group:
   that group is root without a password, and compose must run as root anyway to read the mode-600 env files. Its
   home directory and `.ssh` belong to root and its shell is `/bin/sh`, so it cannot change its keys or start-up
   files. Compose output goes to the VM's journal, not to the public workflow log. The
   host key is pinned in a GitHub variable (`StrictHostKeyChecking yes`); there is no `ssh-keyscan` in the workflow.
9. **Server files come from the owner, not from the deploy key.** `compose.yml`, `Caddyfile`, `vybe-deploy` and
   `vybe-monitor` are installed by `bootstrap.sh` (also `--files-only`). The deploy key cannot change them, so a
   stolen key cannot mount the host or start another image. The workflow compares their SHA-256 on the VM
   (`vybe-deploy status`) with the commit and refuses to deploy on a difference. It also checks that
   `VYBE_PUBLIC_HOST` matches `VYBE_PUBLIC_ORIGIN`.
10. **Deploy and rollback.** `vybe-deploy` pulls both digests and runs `docker compose up -d --wait`. If the new
    release does not become healthy, it starts the current release again and fails. It keeps the previous release
    file: mode `rollback` without inputs goes back one release; with digests and a SHA it deploys an older one.
    The database is never reverted. One API container means a few seconds without `/api` per deploy; Caddy holds
    requests for up to 20 s.
11. **Bootstrap and hardening** (`bootstrap.sh`, idempotent; no `|| true` around configuration changes: a failure
    stops the script): OS updates, unattended security upgrades with a reboot window (04:30 UTC), Docker Engine and
    compose from Docker's apt repository (the key file must hold exactly one key, fingerprint `9DC8 5822 … CD88`), Docker `local` log driver with rotation, `live-restore`, no userland proxy, daemon-wide
    `no-new-privileges`; SSH: keys only, no root, `AllowUsers` admin and deploy user, no forwarding, `MaxAuthTries 6`
    (the new file is tested with `sshd -t` before the reload, the old one comes back on a failure, and `sshd -T`
    must show the effective values); ufw does not govern Docker-published ports, so only `web` publishes (80, 443); ufw (22 limited,
    80, 443) instead of Oracle's iptables-persistent rules; fail2ban for SSH; a 2 GB swap file; journald 500 MB /
    one month; time sync with the OCI time server; a `DOCKER-USER` rule so that containers cannot reach link-local
    addresses (the OCI metadata service). The OCI security list must also open 80 and 443.
12. **Backups on Supabase Free.** `backup.yml` runs `backup.sh` every week on a GitHub runner: `pg_dump` of schema
    `vybe` as the read-only role `vybe_backup` (`backup-grants.sql`; the owner password is only in
    `production-oracle`) through the session pooler with `sslmode=verify-full` (the public Supabase root CA is in
    the repository; its fingerprint matches the root of the live pooler chain), the password in a pgpass file, `age` encryption to public keys in the repository (the private key only with the owner), upload to
    `vybe-exports/backups/`, retention 35 days with at least 4 dumps. The environment `backup` has no reviewers (a
    schedule cannot wait) and allows `master` only. The schedule stays off until the owner's public key is in the
    repository. The API's orphan sweep lists only `exports/`; a test proves it
    never returns `backups/` keys, also with an empty prefix.
13. **Free monitoring.** Docker health checks and restart policy; `vybe-monitor` (systemd timer, 5 minutes) checks
    disk, memory and container health and pings a heartbeat URL (healthchecks.io), `/fail` on a problem, so a dead
    VM also alerts. It restarts a hung API (unhealthy twice in a row and `/health/live` fails), at most 3 times in
    6 hours; when only readiness fails (database), it alerts and does not restart; an external uptime check (UptimeRobot) on `/api/v1/catalog/items`; a heartbeat for the backup
    run, because GitHub disables schedules after 60 days without activity.
14. **CI.** Job `oracle-config` on the arm64 runner: shellcheck of all scripts, `docker compose config` with dummy
    settings plus `jq` rules (ports, subnet, hardening), a native arm64 build of both images, and
    `infra/oracle/test-stack.sh`: the compose stack against PostgreSQL with Caddy's internal CA (TLS, `/`,
    `/api/v1/catalog/items`, headers, 404 for `/health`, http redirect, forged `X-Forwarded-For`, read-only and
    healthy containers). The Fly.io CI job stays.

## Not done

- The Oracle account, the VM, the GitHub environments, the keys and the first deploy: the product owner does them
  (runbook, one-time setup).
- IPv6: the VCN wizard gives IPv4 only. With IPv6, Docker needs IPv6 on the compose network, or published ports
  on IPv6 go through the userland proxy (which is off) and do not work.
- `SSL Mode=Require` in the API and migration connection strings checks no server certificate. The backup uses
  `verify-full`; the same CA file can be used for `VerifyFull` in the API (follow-up, also in ADR 0008).
- A backup of Supabase Auth users, and a second copy of the dumps outside Supabase, where the API's S3 key cannot
  delete them (Supabase S3 keys are not limited to a prefix; runbook).
- CODEOWNERS (`.github/CODEOWNERS`) is in the repository; it is enforced only when the `master` branch rule
  "Require review from Code Owners" is on (an owner step).
- Zero-downtime deploys (two API containers behind Caddy) and a staging VM (the free limit has room for one more).

## Consequences

- Hosting costs USD 0 (Oracle Always Free on a Pay As You Go account with a USD 1 budget alert, Supabase Free, GHCR
  and Actions on a public repository; a domain before real players).
- The team operates the VM: OS updates, Docker updates, restore drills, disk space. Unattended upgrades reboot it
  at most once a night. The Pay As You Go account is mandatory: it avoids the idle reclaim of Always Free instances
  (runbook).
- A deploy is one approved workflow run; a failed migration stops it before the VM changes; a bad image does not
  replace a healthy one.
- A change to the server files needs the owner on the VM (`bootstrap.sh --files-only`) before the next deploy.
- The Fly.io files and `deploy.yml` stay for a later paid move (runbook "Move to Fly.io later").
