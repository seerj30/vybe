# Runbook: backup and restore drill

- Owner: stream D (operations); the on-call primary does the quarterly drill.
- Spec §13: "backups and quarterly restore drills". Spec §12: alert on backup failure. Spec §2 J4: deletion.
- Related: [incident-response.md](incident-response.md), [migrations.md](migrations.md),
  [secret-rotation.md](secret-rotation.md), [ADR 0006](../adr/0006-operations.md).

Hosting ([decision 7](../decisions/07-hosting.md), changed on 2026-11-10): Supabase **Free** for the database
(Pro before the first real beta players), the API and the web app on an Oracle Cloud Always Free VM
([ADR 0009](../adr/0009-oracle-vm.md), [oracle-vm.md](oracle-vm.md)). Supabase Free has **no automatic
backups**: the weekly encrypted dump (section "Weekly dump on Supabase Free") is the only backup until the
upgrade to Pro. This runbook has a provider part (managed backups) and a provider-neutral part (logical dump
with `pg_dump`). Do both when the plan has managed backups. Fly.io (kept, not used): section "Fly.io" at the end.

## What to back up

| Data | Where | Backup |
|---|---|---|
| PostgreSQL schema `vybe` | Database | Provider backups (daily; point-in-time recovery strongly recommended) and a weekly logical dump |
| Deletion log (J4) | Export bucket, prefix `deletion-log/` | It is the backup of the deletion requests. Keep each object at least 90 days (longer than the oldest database backup). |
| Data exports (J4) | Export bucket, prefix `exports/` | No backup. Exports are temporary (24 h) and can be made again. |
| Weekly database dumps (encrypted) | Export bucket, prefix `backups/` | They are the backup. 35 days, at least the newest 4. The API's orphan sweep reads only `exports/` (test `List_NeverReturnsBackups`). |
| Database privileges | [`infra/db/grants.sql`](../../infra/db/grants.sql) | Git. Run the script after every restore. |
| Secrets | Platform secret store | Not in backups. Keep the list of names in [deploy-and-rollback.md](deploy-and-rollback.md). |
| Content (rulesets, catalog, quests) | Git | Git is the source; `seed` loads it again. |

Bucket lifecycle rules: a rule that deletes old exports must apply to the prefix `exports/` only. A rule for
`deletion-log/` deletes objects after 90 days or more, never earlier. Do not add a rule for `backups/`: the
backup script applies its own retention.

Supabase Pro: daily backups are included; the retention depends on the plan (7 days on Pro on 2026-11-07,
to check on the Supabase price page when the project is created). Point-in-time recovery is an add-on
(cost: [deploy-and-rollback.md](deploy-and-rollback.md), "Cost estimate"). Write the retention of the
production project here when it exists: **retention: TBD**.

## What a restore loses, and the deletion log

A restore returns the database to the backup time (the restore point). Two things happen after that point:

1. **Deletion requests (personal data).** A player who asked for deletion after the restore point is in the
   restored database with personal data. This is not allowed (GDPR). The API copies every deletion request
   to the deletion log outside the database every 5 minutes (`Jobs:DeletionLogSeconds`, default 300). The
   `replay-deletions` command reads the log after a restore and anonymizes those players again. It must run
   **before** the API takes traffic. Requests in the last 5 minutes before the failure can be missing from
   the log: check the deletion requests that support received in that time by hand.
2. **Ledger, inventory and progress.** Coins, purchases, XP and quests after the restore point are lost.
   VYBE keeps no second copy of the ledger, so the lost rows cannot be rebuilt. The time window is the
   restore point to the failure: up to 24 hours with daily backups, up to 7 days if only the weekly dump is
   usable, minutes with point-in-time recovery. Use point-in-time recovery in production before the social
   beta. See "Reconcile the ledger gap" below.

## Weekly logical dump (provider-neutral)

Run from a trusted machine or a scheduled job with network access to the database. Use the owner role
through a direct or session-mode connection (not the transaction pooler).

Do not put the password in the command line or the connection string (other users of the machine can see
process arguments). Use a service file and a password file, readable by the job user only:

```ini
# ~/.pg_service.conf (chmod 600)
[vybe-owner]
host=<db host>
port=5432
dbname=postgres
user=<owner role>
sslmode=verify-full
```

```text
# ~/.pgpass (chmod 600): host:port:database:user:password
<db host>:5432:postgres:<owner role>:<password from the secret store>
```

```bash
PGSERVICE=vybe-owner pg_dump --schema=vybe --format=custom --no-owner \
  --file="vybe-$(date -u +%Y%m%dT%H%MZ).dump"
```

- The dump keeps the GRANT and REVOKE statements (no `--no-privileges`). Run `infra/db/grants.sql` after a
  restore anyway: role names can differ between projects.
- Encrypt the file before upload (for example `age` or the storage provider's server-side encryption).
- Store it in a bucket in another account or provider than the database, with a lifecycle rule
  (for example: delete after 35 days). The dump has personal data: same access rules as production.
- A failed dump must alert the on-call people (spec §12 "backup failure").

## Weekly dump on Supabase Free

The workflow [`backup.yml`](../../.github/workflows/backup.yml) runs
[`infra/oracle/backup.sh`](../../infra/oracle/backup.sh) every Sunday at 03:17 UTC (and by hand: Actions →
Backup → Run workflow on `master`):

1. `pg_dump --schema=vybe --format=custom --no-owner` as `vybe_owner` through the session pooler (port 5432),
   with `sslmode=verify-full` against the Supabase root CA. The password is in a pgpass file (mode 600) in a
   private temporary directory, never in an argument.
2. `age` encrypts the stream to the public keys in
   [`infra/oracle/backup-recipients.txt`](../../infra/oracle/backup-recipients.txt). No plain dump is written to disk.
3. Upload to `s3://vybe-exports/backups/vybe-<UTC time>.dump.age` (Supabase Storage S3).
4. Retention: dumps older than 35 days are deleted; the newest 4 always stay.

Limits: the dump has schema `vybe` only. Supabase Auth users (`auth` schema) are not in it. A restore into the
same project keeps them. A restore into a new project has no Auth users: the incident lead decides how players
get access to their accounts again (follow-up: a backup of the Auth users). The dumps are in the
same Supabase project as the database: a lost project loses both. Supabase S3 keys are not limited to a prefix, so
the API's S3 key (on the VM) can also read and delete `backups/`: an attacker with control of the API could delete
the data and the dumps together (the dumps stay encrypted). Before the social beta, copy the newest dump each month
to a place that the VM cannot reach (for example the owner's encrypted disk, or OCI Object Storage, 20 GB Always
Free, with a write-only user).

Deleted players: a dump keeps the personal data of players who asked for deletion after the dump, for up to the
retention (35 days, or longer for the newest 4 dumps when no new dump runs). A restore applies the deletions again
with `replay-deletions` (below). Say this time window in the privacy notice ([decision 8](../decisions/08-legal-wording.md)).

### One-time setup

1. **Backup key** on the owner's Mac (`brew install age`):
   ```bash
   age-keygen -o ~/vybe-backup.key     # prints "Public key: age1..."
   ```
   Put the private key file in the password manager (as a file) and on an offline medium. Delete
   `~/vybe-backup.key` after the restore drill. Open a PR that adds the `age1...` line to
   `infra/oracle/backup-recipients.txt` (public key only). Add the backup on-call person's key the same way.
2. **Supabase root CA**: Supabase dashboard → Project Settings → Database → SSL Configuration → Download
   certificate. Check it, then add it as `infra/oracle/supabase-ca.crt` in the same PR:
   ```bash
   openssl x509 -in prod-ca-2021.crt -noout -subject -enddate -fingerprint -sha256
   ```
   Expected (seen on the pooler's certificate chain on 2026-11-10): subject `CN=Supabase Root 2021 CA`, valid
   until 2031-04-26, SHA-256 fingerprint
   `80:70:25:AD:50:D4:ED:21:9D:2C:9C:7D:29:9C:00:4F:82:4E:B0:0C:F7:F6:5A:FE:F6:07:D0:7B:72:E6:CA:FA`.
   If the dashboard file has another fingerprint, stop and ask the engineering lead.
3. **S3 key for backups**: Supabase → Storage → S3 connection → New access key, named `vybe-backup` (a key
   separate from the API's key, so each can be revoked alone).
4. **GitHub environment `backup`** (Settings → Environments): no required reviewers (a schedule cannot wait for
   an approval); deployment branches: `master` only. This branch rule is the only gate for these secrets (a
   branch could change the workflow file): check it after you create the environment. Secrets:
   ```bash
   gh secret set VYBE_BACKUP_DB_PASSWORD --env backup -R seerj30/vybe              # the vybe_owner password
   gh secret set VYBE_BACKUP_S3_ACCESS_KEY_ID --env backup -R seerj30/vybe         # vybe-backup key id
   gh secret set VYBE_BACKUP_S3_SECRET_ACCESS_KEY --env backup -R seerj30/vybe     # vybe-backup secret
   gh secret set VYBE_BACKUP_PING_URL --env backup -R seerj30/vybe                 # optional, see below
   ```
5. **Alert on a failed or missing backup** (spec §12 "backup failure"): GitHub emails the person who last changed
   the schedule when a run fails. GitHub also disables schedules after 60 days without repository activity, and
   then nothing runs and nothing fails. So make a free healthchecks.io check with period 7 days and grace 1 day,
   email to the on-call people, and put its ping URL in `VYBE_BACKUP_PING_URL`. A failed run pings `/fail`.
6. Run the workflow once by hand. Check the log ("backup: uploaded backups/vybe-….dump.age") and the object in
   Storage → `vybe-exports` → `backups/`.

### Restore from a weekly dump

On a trusted machine with the private key (never on the VM):

```bash
d=$(mktemp -d)                                                   # mode 700
AWS_ENDPOINT_URL_S3=https://pkrudxjeihxrkkdlfdmr.storage.supabase.co/storage/v1/s3 aws s3 ls s3://vybe-exports/backups/
AWS_ENDPOINT_URL_S3=https://pkrudxjeihxrkkdlfdmr.storage.supabase.co/storage/v1/s3 \
  aws s3 cp s3://vybe-exports/backups/vybe-<time>.dump.age "$d/"
age --decrypt -i <private key file> "$d/vybe-<time>.dump.age" \
  | PGSERVICE=vybe-restore pg_restore -d vybe --no-owner --exit-on-error
rm -rf "$d"
```

The S3 key comes from the AWS CLI's normal credential prompt or environment (`aws configure` with a temporary
profile, removed afterwards), with region `eu-central-1` and `addressing_style = path`. Then continue with step 5
of the drill (grants), or step 3 of "Restore production after data loss".

## Quarterly restore drill

Goal: prove that a backup restores to a working API, that deletions are applied again, and measure the
time (RTO) and the data loss (RPO).

The drill never uses production storage or production workers. The drill API runs with
`Jobs__Enabled=false` and with a separate drill bucket, because the privacy processor deletes export files
that the restored database does not know (orphan sweep): with the production bucket it would delete live
exports.

1. Create an empty PostgreSQL 17 database that is isolated from production (a new Supabase project, or
   `docker run -d --name vybe-restore -e POSTGRES_PASSWORD=<local> -p 127.0.0.1:55434:5432 postgres:17.6-alpine`).
2. Create a drill bucket and drill access keys. Copy the production deletion log into it with a read-only
   production key, for example:
   `aws s3 sync s3://<prod bucket>/deletion-log/ s3://<drill bucket>/deletion-log/ --endpoint-url <url>`.
3. Write down the start time.
4. Restore the newest backup:
   - Provider backup: use the provider's "restore to new project" function.
   - Logical dump (service `vybe-restore` in the service file, password in `~/.pgpass`):
     ```bash
     PGSERVICE=vybe-restore createdb vybe
     PGSERVICE=vybe-restore pg_restore -d vybe --no-owner --exit-on-error vybe-<time>.dump
     ```
5. Create the API role if it does not exist (`CREATE ROLE vybe_api LOGIN PASSWORD ...`), then apply the
   privileges as the owner role:
   ```bash
   PGSERVICE=vybe-restore psql -d vybe -v ON_ERROR_STOP=1 -v api_role=vybe_api -f infra/db/grants.sql
   ```
6. Run the image of the current production release against the restored database, with the drill settings
   (`ASPNETCORE_ENVIRONMENT=Staging`, the drill bucket and keys in `Storage__Exports__*`, `Jobs__Enabled=false`):
   1. `migrate` with the owner role: it must apply nothing, or only migrations newer than the backup.
   2. `replay-deletions` with the API role in `ConnectionStrings__Vybe`. Exit code 0 = no deletion left
      queued. The log line "Deletion replay: …" gives the counts.
   3. The API with `ConnectionStrings__Vybe` set to the **API role** (not the owner).
7. Check:
   - `GET /health/ready` returns 200.
   - The API role has data access and nothing more:
     ```sql
     SELECT has_table_privilege('vybe_api', 'vybe.players', 'SELECT, INSERT, UPDATE, DELETE') AS api_ok,
            has_schema_privilege('vybe_api', 'vybe', 'CREATE') AS api_can_create,
            EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon')
              AND has_schema_privilege('anon', 'vybe', 'USAGE') AS anon_can_use;
     ```
     Expected: `api_ok` true, `api_can_create` false, `anon_can_use` false.
   - Every logged deletion is applied: for each player id in the deletion log that exists in the restored
     database, `status = 'deleted'`.
   - Row counts are plausible:
     ```sql
     SELECT (SELECT count(*) FROM vybe.players) AS players,
            (SELECT count(*) FROM vybe.wallet_transactions) AS ledger,
            (SELECT max(created_at) FROM vybe.wallet_transactions) AS newest_ledger_row;
     ```
   - Wallet balances match the ledger (a wallet starts at 0; every change is a ledger row with `delta`):
     ```sql
     SELECT count(*) AS mismatches FROM vybe.wallets w
     WHERE w.balance <> (SELECT coalesce(sum(t.delta), 0) FROM vybe.wallet_transactions t WHERE t.player_id = w.player_id);
     ```
     The result must be 0.
   - A guest can be created and can play one run against the restored API.
8. Write down the end time. RTO = end − start. RPO = start − `newest_ledger_row`.
9. Delete the restored database, the drill bucket and keys, and every copy of the dump on the drill machine.
10. Record the drill in the table below. Open an issue for each problem.

## Drill record

| Date | Backup used | RTO | RPO | Deletions replayed | Result | Done by |
|---|---|---|---|---|---|---|
| — | — | — | — | — | No drill yet | — |

## Restore production after data loss

Do this only as part of an incident ([incident-response.md](incident-response.md)), with the incident lead
and the privacy contact.

1. Stop writes: scale the API to zero, or set the edge to return 503 for `/api`. Write down the failure time.
2. Restore to a **new** database (never over the damaged one); keep the damaged one for analysis.
3. Apply the privileges: `infra/db/grants.sql` as the owner role (step 5 of the drill).
4. Run `migrate` with the owner role.
5. Run `replay-deletions` with the **production** settings (production bucket, which holds the deletion
   log) and the new database. Do not continue until the exit code is 0.
6. Check the deletion requests that support received in the 5 minutes before the failure. For each one
   that is missing from the log, write `deletion-log/<requestId>.json` with
   `{"requestId":"<new uuid>","playerId":"<player id>","requestedAt":"<UTC time>"}`, then run
   `replay-deletions` again.
7. Change `ConnectionStrings__Vybe` and `ConnectionStrings__Migrations` to the new database
   ([secret-rotation.md](secret-rotation.md)), then start the API.
8. Reconcile the ledger gap (below), and tell players about the lost time window.

## Reconcile the ledger gap

The rows between the restore point and the failure are gone, and VYBE has no other copy of them.

1. Write down the restore point (`newest_ledger_row` of the restored database) and the failure time. This is
   the gap.
2. Do not try to rebuild balances from logs: the logs have no amounts and no player ids (by design, no
   personal data).
3. The product owner decides the compensation (for example a fixed coin grant for every player active in the
   gap). Players active in the gap are not listed in the restored database; use support requests and the
   players' own reports.
4. Apply each compensation with the admin wallet adjustment (audited, with an `Idempotency-Key` per player),
   never with SQL.
5. Write the gap, the number of players compensated and the total coins in the incident review.

## Oracle VM

The VM keeps no player data: the database and the bucket are on Supabase. On the VM, only the Caddy certificates
(`/var/lib/vybe/caddy`, made again on demand) and the settings files (`/etc/vybe`, made again with `stage-env.sh`
from the password manager) exist. A lost VM is a new VM: [oracle-vm.md](oracle-vm.md), one-time setup steps 4–7.

For "Restore production after data loss":

- Step 1, stop writes: on the VM, `vc stop api` ([oracle-vm.md](oracle-vm.md), "Restart, logs, one-off commands").
- Steps 2–4: restore the newest weekly dump ("Restore from a weekly dump") into a **new** Supabase project, or
  into the existing project only with the approval of the incident lead. Then grants and `migrate` (workflow
  mode `release-only` with the new owner string).
- Step 5, `replay-deletions`: after `stage-env.sh` has the new API connection string, on the VM:
  `vc run --rm --no-deps api replay-deletions`. Do not continue until the exit code is 0.
- Step 7: `stage-env.sh` (new values), the new owner string in `VYBE_MIGRATIONS_CONNECTION_STRING` and
  `VYBE_BACKUP_DB_PASSWORD`, then `vc up -d --wait`. A new project ref also changes the constants in
  `stage-env.sh` and `backup.sh` (a PR), the Supabase URLs and the anon key.

## Fly.io (kept, not used)

The API on Fly.io keeps no data: no volumes, and the release machine keeps nothing. Only the database
(Supabase) and the export bucket (Supabase Storage) need backups. Images stay in `registry.fly.io` and can
be built again from git.

For "Restore production after data loss":

- Step 1, stop writes: `fly scale count 0 -a vybe-prod-api`. The web app stays up; `/api` answers 502.
  Write down `fly scale show -a vybe-prod-api` first, to scale back to the same count.
- Step 2: on Supabase, use "Restore to a new project" (Database → Backups) if the plan offers it. An
  in-place restore overwrites the damaged database: only with the approval of the incident lead.
- Steps 3–6 (grants, `migrate`, `replay-deletions`): run them from a trusted machine with the image of the
  current release (`fly releases -a vybe-prod-api --image`), as in the drill, but with the production bucket
  settings. Do not use `fly machine run` here: it starts the machine with the old connection strings of
  the app. Give each secret through the environment, never as an argument or a file in `/tmp`:
  ```bash
  read -rs ConnectionStrings__Migrations && export ConnectionStrings__Migrations   # paste, then Enter
  docker run --rm -e ConnectionStrings__Migrations registry.fly.io/vybe-prod-api@sha256:<digest> migrate
  ```
  Use the same form for `replay-deletions` (`ConnectionStrings__Vybe` and the `Storage__Exports__*`
  settings). Close the shell afterwards.
- Step 7: put the new API connection string in with `fly secrets import --stage -a vybe-prod-api` (see the
  deploy runbook, one-time setup step 8), and the new owner string in the GitHub environment secret
  `VYBE_MIGRATIONS_CONNECTION_STRING` (step 10; it is never a Fly secret). Then `fly secrets deploy -a vybe-prod-api` and
  `fly scale count <count> -a vybe-prod-api --region <region>`. Check `fly checks list -a vybe-prod-api`.
- A new Supabase project has a new project ref: also change `Auth__Supabase__*`, `Storage__Exports__*`,
  and the web settings `VYBE_SUPABASE_URL` and `VYBE_SUPABASE_ANON_KEY` (`fly secrets set -a vybe-prod-web`).
  Players must sign in again.
