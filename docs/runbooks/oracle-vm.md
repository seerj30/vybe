# Runbook: production on the Oracle Cloud VM

- Owner: stream D (operations); the product owner does the account and VM steps, guided by the orchestrator.
- Applies to: production since decision 7 changed on 2026-11-10 (no paid hosting): the API and the web app run
  on **one Oracle Cloud Always Free ARM VM in Frankfurt**, with Docker Compose and Caddy. Design:
  [ADR 0009](../adr/0009-oracle-vm.md). The database and Auth stay on Supabase (Free, `eu-central-1`).
- Related: [deploy-and-rollback.md](deploy-and-rollback.md) (settings, generic procedure),
  [backup-restore.md](backup-restore.md) (weekly dump, restore), [secret-rotation.md](secret-rotation.md),
  [incident-response.md](incident-response.md). The Fly.io design ([ADR 0008](../adr/0008-fly-deployment.md)) is
  superseded; its files were removed on 2026-11-10.

## Rules for every step with a secret

- Never put a password, key or token in a command argument, a URL, a file in `/tmp`, a ticket or a chat.
- Paste a secret only at a prompt that reads stdin without echo (`stage-env.sh`, `gh secret set`, `read -rs`).
- The settings files on the VM are `/etc/vybe/api.env` and `/etc/vybe/web.env`: `root:root`, mode `600`. Only
  `stage-env.sh` writes them. Never copy them off the VM.
- The owner credential (`vybe_owner`) is never on the VM. It is in the GitHub environments only (release step,
  backup).

## What runs where

| Part | Where | File |
|---|---|---|
| Caddy: TLS (Let's Encrypt), web app, `/api` proxy, headers | VM, container `vybe-web-1`, ports 80 and 443 | [`infra/oracle/Caddyfile`](../../infra/oracle/Caddyfile), web image [`infra/web/Dockerfile`](../../infra/web/Dockerfile) |
| API (workers in-process) | VM, container `vybe-api-1`, **no published port** | [`infra/oracle/compose.yml`](../../infra/oracle/compose.yml), [`services/api/Dockerfile`](../../services/api/Dockerfile) |
| Release step (migrate, then seed, owner role) | GitHub runner, before the VM changes | [`.github/workflows/deploy-oracle.yml`](../../.github/workflows/deploy-oracle.yml) |
| Images (`linux/arm64`) | GHCR: `ghcr.io/seerj30/vybe-api`, `ghcr.io/seerj30/vybe-web`, public | same workflow |
| PostgreSQL, Auth, export bucket | Supabase `vybe-prod` (Free) | — |
| Weekly encrypted database dump | Bucket `vybe-exports`, prefix `backups/` | [`infra/oracle/backup.sh`](../../infra/oracle/backup.sh), [`.github/workflows/backup.yml`](../../.github/workflows/backup.yml) |

Request path: browser → VM port 443 (Caddy ends TLS) → `api:8080` over the compose network `172.30.80.0/24`.
Docker publishes the ports with DNAT (no userland proxy), so Caddy sees the real client address. Caddy sends it
as the only `X-Forwarded-For` value. The API trusts forwarded headers only from `172.30.80.0/24`.

Files on the VM:

| Path | Content | Written by |
|---|---|---|
| `/opt/vybe/compose.yml`, `/opt/vybe/Caddyfile` | the files of `infra/oracle/` | `bootstrap.sh` |
| `/opt/vybe/bin/vybe-deploy`, `/opt/vybe/bin/vybe-monitor` | `deploy.sh`, `monitor.sh` | `bootstrap.sh` |
| `/opt/vybe/release.env`, `release.previous.env` | image digests and git SHA (no secrets) | `vybe-deploy` |
| `/etc/vybe/api.env`, `/etc/vybe/web.env` | settings and secrets (mode 600) | `stage-env.sh` |
| `/etc/vybe/monitor.env` | heartbeat URL (mode 600) | the owner (step 9) |
| `/var/lib/vybe/caddy` | certificates and the ACME account | Caddy |

Users on the VM: `ubuntu` (the admin, SSH key of the owner's Mac, sudo) and `vybe-deploy` (the GitHub deploy key;
it can run only `vybe-deploy`; no shell, no docker group).

A shell function for the commands below (run it on the VM as `ubuntu`):

```bash
vc() { sudo docker compose -p vybe -f /opt/vybe/compose.yml --env-file /opt/vybe/release.env "$@"; }
```

## Host name: domain or sslip.io

Caddy gets a certificate from Let's Encrypt for `VYBE_PUBLIC_HOST`. Let's Encrypt needs a DNS name, not an IP.

| Option | Host name | Cost | Notes |
|---|---|---|---|
| **No domain (first deploy and tests only)** | `<public IPv4 with dashes>.sslip.io`, for example `130-61-10-20.sslip.io` | 0 | sslip.io answers with the IP in the name. No DNS set-up. A new IP is a new host name: so the reserved public IP of one-time setup step 4 is mandatory. sslip.io is a third party that you do not control. |
| **Own domain (required before real players)** | for example `play.vybe.example` | the domain | An `A` record to the reserved public IP. You control the name, it survives a new VM, and it does not depend on sslip.io. |

The host name is the web origin `https://<host>`. When it changes, change all of these together:

1. `stage-env.sh` on the VM (it writes `VYBE_PUBLIC_HOST` and `Challenges__ShareBaseUrl=https://<host>`).
2. Supabase → Authentication → URL configuration: **Site URL** `https://<host>` and **Redirect URLs**
   `https://<host>/`. Today they are `https://vybe-prod-web.fly.dev`: change them before the first sign-in test.
   Sign-in and email links go to the Site URL.
3. GitHub environment variable `VYBE_PUBLIC_ORIGIN` = `https://<host>`.
4. A deploy (or "Restart" below), so the containers read the new values.

**Before the first real players, move to an own domain.** Do steps 1–4 above with the domain, then check: sign-in
with a test account (Supabase URLs), a new invite link opens on the domain (`Challenges__ShareBaseUrl`), and the
domain answers with HSTS. Keep the sslip.io name working for some days (the IP stays the same), so links that
testers already have still open.

Effects of a change: invite links that players already shared (`Challenges__ShareBaseUrl`) point to the old
host name. They stop working when the old name no longer reaches the VM (sslip.io with a new IP, or a removed
DNS record). Email links that Supabase already sent go to the old Site URL. The HSTS header has
`includeSubDomains`: with an own domain, every subdomain of the host must be on https.

## One-time setup

Do the steps in this order. Keep each value in the password manager.

1. **Oracle Cloud account.** Sign up at `cloud.oracle.com` with the product owner's details.
   - **Home region: Germany Central (Frankfurt), `eu-frankfurt-1`.** You cannot change the home region later,
     and Always Free compute runs only in the home region. Supabase is in `eu-central-1` (Frankfurt).
   - Turn on multi-factor authentication for the account. Keep the recovery codes.
   - **Pay As You Go: recommended, not used for now.** Oracle reclaims (stops) Always Free instances of free
     accounts that stay under its idle thresholds for 7 days (CPU 95th percentile, network and memory all under
     20 %), and a small beta is under them. Pay As You Go accounts (Billing → Upgrade and manage payment) keep
     the Always Free resources at no charge and are not reclaimed; set a budget alert of USD 1 with it
     (Billing → Budgets). **Decision of the product owner (2026-11-10): stay on Free Tier** (no upgrade). The
     accepted risk: after a quiet week Oracle can stop the VM; start it in the console (Compute → Instances →
     Start), and the containers start by themselves. The heartbeat and the uptime monitor show the stop.
     Upgrade before real beta players, when an outage matters.
2. **SSH key on the owner's Mac** (two keys: one for the admin, one for GitHub):
   ```bash
   ssh-keygen -t ed25519 -f ~/.ssh/vybe-admin -C vybe-admin      # use a passphrase
   ssh-keygen -t ed25519 -f ~/.ssh/vybe-deploy -C vybe-deploy -N ""   # for the GitHub secret only
   ```
3. **Network.** Networking → Virtual cloud networks → Start VCN wizard → "Create VCN with Internet
   Connectivity". In the public subnet's security list, add ingress rules: TCP 80 and TCP 443 from `0.0.0.0/0`.
   Keep TCP 22. Remove nothing else. The security list is the outer firewall. On the VM, ufw allows 22, 80 and
   443, but **ufw does not govern ports that Docker publishes** (Docker's own iptables rules come first). So never
   publish another port in `compose.yml`; the CI job checks that only `web` publishes 80 and 443.
4. **Reserved public IP (mandatory, before the VM).** Networking → IP management → Reserved public IPs →
   Reserve public IP address, name `vybe-prod`, in the home region. The host name (sslip.io or the domain's `A`
   record), the GitHub variables and the host key line all use this IP; a reserved IP stays when you replace the
   VM. Check on the price list that it costs nothing in your account (the budget alert also tells you).
5. **VM.** Compute → Instances → Create instance:
   - Image: Canonical **Ubuntu 26.04** or **24.04** LTS (aarch64; the "Minimal" image is also good). Production runs
     26.04 since 2026-11-10.
   - Shape: **VM.Standard.A1.Flex** (Ampere), **2 OCPU, 12 GB** memory. The Always Free limit is 4 OCPU and
     24 GB in total, so a second VM of the same size for a drill or staging is also free.
   - Boot volume: 50 GB (the free total is 200 GB).
   - Networking: the public subnet of step 3, "Do not assign a public IPv4 address" (the reserved one comes next).
   - SSH key: paste `~/.ssh/vybe-admin.pub`.
   - "Out of capacity": try another availability domain, or try again later (Pay As You Go helps).
   - After the VM is running: Instance → Attached VNICs → the VNIC → IPv4 addresses → Edit → "Reserved public
     IP" → `vybe-prod`. Write down this IP: it is `<ip>` below.
6. **Bootstrap the VM.** From the repository on the Mac, at a commit of `master`:
   ```bash
   ssh -o IdentitiesOnly=yes -i ~/.ssh/vybe-admin ubuntu@<ip>                         # accept the host key once, then exit
   scp -o IdentitiesOnly=yes -i ~/.ssh/vybe-admin -r infra/oracle ubuntu@<ip>:vybe-oracle
   scp -o IdentitiesOnly=yes -i ~/.ssh/vybe-admin ~/.ssh/vybe-deploy.pub ubuntu@<ip>:vybe-oracle/
   ssh -o IdentitiesOnly=yes -i ~/.ssh/vybe-admin ubuntu@<ip> sudo bash vybe-oracle/bootstrap.sh --deploy-key vybe-oracle/vybe-deploy.pub
   ```
   The script updates the OS, sets unattended security upgrades (reboot at 04:30 UTC when necessary), installs
   Docker from Docker's repository (key checked by fingerprint), sets Docker log rotation, makes the user
   `vybe-deploy`, sets SSH to keys only (no root login, only `ubuntu` and `vybe-deploy`, `MaxAuthTries 6`; tested
   with `sshd -t` before the reload and checked with `sshd -T` after it), replaces Oracle's
   iptables rules with ufw (22, 80, 443), turns on fail2ban, adds a 2 GB swap file, limits journald, sets time
   sync, blocks container access to the OCI metadata service, installs the server files and the `vybe-monitor`
   timer. At the end it prints the SSH host key line.
   Copy it, then reboot:
   ```bash
   ssh -o IdentitiesOnly=yes -i ~/.ssh/vybe-admin ubuntu@<ip> sudo reboot
   ```
   After the reboot, log in again in a **new** terminal before you close the old one (SSH still works).
   Compare the host key fingerprint that `ssh` shows with the one that the script printed.
7. **Stage the settings** on the VM:
   ```bash
   ssh -o IdentitiesOnly=yes -i ~/.ssh/vybe-admin ubuntu@<ip>
   bash vybe-oracle/stage-env.sh
   ```
   It suggests `<ip-with-dashes>.sslip.io` as the host name (press Enter) or takes your domain. Then it asks for
   five values without echo: the `vybe_api` password, the anon key, the service_role key, and the S3 key pair of
   the API (password manager, "VYBE - vybe_api" and "VYBE - Supabase S3"). It makes the invite secret itself
   (32 random bytes) and keeps it when you run it again. It prints the names only. Then do "Host name" steps
   2 and 3 (Supabase URLs, `VYBE_PUBLIC_ORIGIN`).
8. **GitHub environment `production-oracle`** (Settings → Environments → New environment):
   - Required reviewers: the engineering lead and the on-call primary; "Prevent self-review" on when two
     people can approve.
   - Deployment branches and tags: `master` and `v*` only. With `workflow_dispatch`, the workflow file of the
     chosen branch runs, so this rule (not the check in the workflow) keeps the secrets away from other branches.
   - Variables:
     - `VYBE_PUBLIC_ORIGIN` = `https://<host>`
     - `VYBE_VM_HOST` = the public IPv4
     - `VYBE_VM_HOST_KEY` = `<ip> ssh-ed25519 AAAA…` (the line from step 6, with the IP first)
   - Secrets (environment secrets, not repository secrets):
     ```bash
     gh secret set VYBE_VM_SSH_KEY --env production-oracle -R seerj30/vybe < ~/.ssh/vybe-deploy
     gh secret set VYBE_MIGRATIONS_CONNECTION_STRING --env production-oracle -R seerj30/vybe   # paste the owner string
     ```
     The owner string uses the session pooler (port 5432):
     `Host=aws-0-eu-central-1.pooler.supabase.com;Port=5432;Database=postgres;Username=vybe_owner.pkrudxjeihxrkkdlfdmr;Password=<password>;SSL Mode=Require`.
   - Then delete the private key file from the Mac: `rm ~/.ssh/vybe-deploy` (GitHub has the only copy; a new
     key is "Secret rotation" below).
9. **Monitoring** (free; see "Monitoring"): make the heartbeat check and the uptime check.
10. **Backup** (free; [backup-restore.md](backup-restore.md), "Weekly dump on Supabase Free"): the age key (and the
    PR that adds the public key and turns on the schedule), the read-only role `vybe_backup`, the S3 key
    `vybe-backup`, the GitHub environment `backup`.
11. **Dry run and images.** Actions → "Deploy (Oracle VM)" → Run workflow on `master`, mode `release-only`. A
    reviewer approves. The run builds both images for `linux/arm64`, runs `release` (migrate, then seed) on the
    runner, and pushes both images to GHCR. The log must show "The database schema is up to date" and
    "Seeded … rulesets, … catalog items".
12. **Make the images public** (no secrets are in the images; the VM then needs no registry credential):
    GitHub → your profile → Packages → `vybe-api` → Package settings → Change visibility → Public. The same for
    `vybe-web`. Check "Manage Actions access": the repository `seerj30/vybe` has the role "Write".
13. **Grants** (if not done before): as the owner role, with the service `vybe-owner` in
    `~/.pg_service.conf` and the password in `~/.pgpass` (both mode 600):
    ```bash
    PGSERVICE=vybe-owner psql -v ON_ERROR_STOP=1 -v api_role=vybe_api -f infra/db/grants.sql
    PGSERVICE=vybe-owner psql -v ON_ERROR_STOP=1 -f infra/oracle/backup-grants.sql   # after the role of step 10
    ```

## First deploy

1. Checklist. Do not start until every line is true:
   - The Oracle account is **Pay As You Go** with the **USD 1 budget alert** (step 1).
   - The VM has the **reserved public IP** (steps 4–5); `VYBE_VM_HOST` and `VYBE_VM_HOST_KEY` use it.
   - Steps 6–13 are done; the Supabase Site URL and redirect URL are `https://<host>`.
   - The heartbeat and the uptime check exist (step 9), and the backup is set up (step 10).
   - CI on `master` is green.
2. Actions → "Deploy (Oracle VM)" → Run workflow on `master`, mode `deploy`. A reviewer approves.
3. The workflow:
   1. checks that the commit is on `master` and has a green CI run;
   2. job `build` (no secrets): builds both images natively for `linux/arm64`, pushes them to GHCR and signs a
      provenance attestation for each digest;
   3. job `deploy` (environment `production-oracle`, the approval): verifies both attestations (built by this
      workflow, from this commit, on a GitHub-hosted runner), runs `release` on the runner, then writes the SSH
      key and checks that the VM's `compose.yml`, `Caddyfile` and `vybe-deploy` are the files of this commit and
      that the host name matches `VYBE_PUBLIC_ORIGIN`;
   4. deploys the images **by digest** (the run summary lists both digests and the SHA), then removes the key;
   5. on the VM, `vybe-deploy` pulls the images and runs `docker compose up -d --wait`: it waits until both
      health checks pass (API: `/health/ready`). A release that does not become healthy is replaced by the
      current release again, and the run fails;
   6. smoke test from outside through Caddy: `/` (CSP, HSTS, `no-referrer`), `/api/v1/catalog/items`,
      `/health/ready` is 404, http redirects to https. The first deploy waits up to 2 minutes for the
      certificate.
4. Do the smoke test by hand (below).

## Deploy

Every later deploy is the same run (mode `deploy`, on `master` or a `v*` tag). The API container restarts, so
`/api` waits a few seconds (Caddy holds requests up to 20 s); the web app stays up.

When a PR changes `infra/oracle/compose.yml`, `Caddyfile`, `deploy.sh` or `monitor.sh`, the deploy key cannot
install them (by design). After the merge, install them, then deploy:

```bash
scp -o IdentitiesOnly=yes -i ~/.ssh/vybe-admin -r infra/oracle ubuntu@<ip>:vybe-oracle     # from the merged master
ssh -o IdentitiesOnly=yes -i ~/.ssh/vybe-admin ubuntu@<ip> sudo bash vybe-oracle/bootstrap.sh --files-only
```

The workflow refuses to deploy while the files differ ("The VM's compose.yml differs from this commit").

## Smoke test

1. `vc ps`: both containers are `Up … (healthy)`.
2. Open `https://<host>` in a private browser window. The landing page shows with its styles. The browser
   console shows no CSP error. The certificate is from Let's Encrypt.
3. Guest with the age gate: "Start as guest", give the birth month and year of an adult. The avatar page opens.
4. Profile: save the avatar, open `/hub`, open your own profile; the level and wallet show.
5. Sign-in: create an account with a test email, confirm it, link the guest. The player id stays the same in
   `/settings`. Delete the test account afterwards in `/settings`.
6. Client address: from one machine, send 11 guest creations within one minute, each with another forged
   `X-Forwarded-For` header:
   ```bash
   for i in $(seq 1 11); do curl -s -o /dev/null -w '%{http_code} ' -X POST -H 'Content-Type: application/json' \
     -H 'X-Requested-With: vybe-web' -H "X-Forwarded-For: 198.51.100.$i" \
     -d '{"birthYear":1990,"birthMonth":6}' https://<host>/api/v1/guests; done
   ```
   The 11th answer must be 429. If all are 201, stop and treat it as a SEV-2 (the guest rate limit does not
   work). CI runs the same check against the compose stack on every PR (`infra/oracle/test-stack.sh`).

## Rollback

A rollback changes images only. It never reverts the database (expand-contract, [migrations.md](migrations.md)).

- **To the release before the current one:** Actions → "Deploy (Oracle VM)", mode `rollback`, all image inputs
  empty. `vybe-deploy` starts `release.previous.env` again (the two releases change places).
- **To an older release:** mode `rollback` with `api_image`, `web_image` and `release_sha` from the summary of
  that release's run. The SHA must be on `master`, and both images must have the attestation of this workflow for
  that SHA; other images are refused.
- **Without GitHub** (GitHub down): on the VM, `sudo /opt/vybe/bin/vybe-deploy rollback`.

If the old image does not start on the new schema, roll forward with a fix ([deploy-and-rollback.md](deploy-and-rollback.md)).

## Restart, logs, one-off commands

| Task | Command on the VM |
|---|---|
| Status | `vc ps`; `sudo /opt/vybe/bin/vybe-deploy status` |
| Logs (JSON, no request paths, no secrets) | `vc logs --tail 200 api`; `vc logs --tail 200 web` |
| Restart after `stage-env.sh` | `vc up -d --force-recreate --wait` |
| Stop writes (incident) | `vc stop api` (the web app stays; `/api` answers 502). Start: `vc up -d --wait` |
| `replay-deletions` (after a restore) | `vc run --rm --no-deps api replay-deletions` (API role and bucket from `api.env`) |
| `migrate`, `seed`, `release` | Only the workflow, mode `release-only`. The VM has no owner credential. |

## Monitoring

All free. Alerts go to the on-call people ([incident-response.md](incident-response.md)).

| Check | What it finds | Set-up |
|---|---|---|
| Docker health checks and `restart: unless-stopped` | A crashed container restarts; `--wait` stops a bad deploy | `compose.yml` |
| API auto-restart (`vybe-monitor`) | A hung API: unhealthy in 2 runs in a row (10 minutes) **and** `/health/live` fails. At most 3 restarts in 6 hours, then alerts only. When only readiness fails (database outage, pending migration), it alerts and does not restart. | `monitor.sh`; state in `/var/lib/vybe/monitor` |
| **Heartbeat** (`vybe-monitor`, every 5 minutes) | Disk over 85 %, memory over 90 %, a container not healthy, the VM down | Make a free check at healthchecks.io (period 5 min, grace 10 min, email to on-call). On the VM: `sudo bash -c "umask 077; read -rs u; printf \"VYBE_MONITOR_PING_URL='%s'\n\" \"\$u\" > /etc/vybe/monitor.env"` and paste the ping URL. Test: `sudo /opt/vybe/bin/vybe-monitor`. |
| **Uptime from outside (GitHub, no account)** | The web app, `/api` through Caddy, and a certificate with less than 14 days left | `.github/workflows/uptime.yml`, every 15 minutes. A failure opens one issue labelled `production-down` (GitHub emails the owner); a later pass closes it. Start it by hand: Actions → "Uptime (production)" → Run workflow. Change the address with the repository variable `VYBE_UPTIME_ORIGIN`. GitHub can delay scheduled runs and stops them after 60 days without repository activity. |
| **Uptime from outside** | DNS, certificate, Caddy, the API path | A free UptimeRobot HTTP(S) monitor on `https://<host>/api/v1/catalog/items`, every 5 minutes, alert after 2 failures. Also turn on its certificate expiry alert. |
| Backup heartbeat | No successful weekly dump | [backup-restore.md](backup-restore.md) |
| Problems in the journal | Details of a heartbeat failure | `journalctl -t vybe-monitor --since -1d` |

Log rotation: Docker's `local` log driver keeps at most 5 × 20 MB per container; journald keeps at most
500 MB and one month. Unused images older than 30 days are deleted after each deploy. Disk use:
`df -h /` and `sudo docker system df`.

## OS and Docker updates

**Release upgrades** (for example 24.04 → 26.04) are never automatic: `bootstrap.sh` sets `Prompt=never`, so the login
message does not offer `do-release-upgrade`. Do one only as a planned change: back up, run it in `screen`, reboot,
run `bootstrap.sh` again (it accepts 24.04 and 26.04 and switches the Docker repository to the new release), then
deploy. On 2026-11-10 an unplanned upgrade from the login offer moved production to 26.04 between two deploy steps.

- Security updates install every day (unattended-upgrades). A reboot, when necessary, happens at 04:30 UTC;
  the containers start again by themselves (about one minute without service).
- Once a month, in a quiet hour, as `ubuntu`:
  ```bash
  sudo apt-get update && sudo apt-get full-upgrade      # also Docker (live-restore keeps the containers up)
  [ -f /var/run/reboot-required ] && sudo reboot
  ```
  Then check `vc ps` and the smoke test.
- Base images (Caddy, .NET, Node, PostgreSQL client) are pinned by digest in the repository: update them by PR.

## Secret rotation

| Secret | How |
|---|---|
| `vybe_api` password, anon/service_role keys, API S3 key | Change it at Supabase, then `stage-env.sh` on the VM, then "Restart". |
| Invite secret | `stage-env.sh --new-invite-secret` (ends every open invite), then "Restart". |
| Backup role password | Supabase (`\password vybe_backup`), then `gh secret set VYBE_BACKUP_DB_PASSWORD --env backup`. |
| Deploy key | Make a new key (one-time setup step 2), copy the `.pub` file to `vybe-oracle/` on the VM, run `sudo bash vybe-oracle/bootstrap.sh --deploy-key vybe-oracle/vybe-deploy.pub` (it replaces the old key), then `gh secret set VYBE_VM_SSH_KEY …` (step 8) and delete the private file. |
| VM host key (new VM) | New `VYBE_VM_HOST_KEY` variable from the bootstrap output. |
| Owner password | Supabase, then `gh secret set VYBE_MIGRATIONS_CONNECTION_STRING --env production-oracle`. The owner password is in no other environment. |

Write each rotation in the record of [secret-rotation.md](secret-rotation.md).

## Restore drill on Oracle

Do the quarterly drill of [backup-restore.md](backup-restore.md) with the newest weekly dump. Free places for the
drill database: a local `postgres:17` container on the owner's Mac, or a second Always Free VM (the free limit has
room for one more 2 OCPU / 12 GB VM). Never restore into the production project during a drill.

## Idle reclaim

Oracle can reclaim (stop) Always Free instances that are idle for 7 days: CPU 95th percentile under 20 %,
network under 20 %, and memory under 20 % (A1 shapes). A beta with few players can look idle.

- **Mandatory:** the account is Pay As You Go (one-time setup step 1). Always Free resources stay free, and
  Oracle does not reclaim idle instances of paid accounts. Keep the USD 1 budget alert.
- Do not run load only to look busy: it uses the shared free capacity and Oracle can change the rule.
- If Oracle stops the VM: start it in the console (Compute → Instances → Start). The containers start by
  themselves. The heartbeat and the uptime monitor show the outage.

## Cost

| Item | Monthly cost |
|---|---|
| Oracle VM (A1, 2 OCPU, 12 GB, 50 GB boot volume, 10 TB outbound) | 0 (Always Free) |
| Supabase Free (until the upgrade to Pro, decision 7) | 0 |
| GHCR (public images), GitHub Actions (public repository) | 0 |
| sslip.io, Let's Encrypt, healthchecks.io, UptimeRobot (free plans) | 0 |
| A domain (optional) | the registrar's price |
