# Runbook: backup and restore drill

- Owner: stream D (operations); the on-call primary does the quarterly drill.
- Spec §13: "backups and quarterly restore drills". Spec §12: alert on backup failure. Spec §2 J4: deletion.
- Related: [incident-response.md](incident-response.md), [migrations.md](migrations.md),
  [secret-rotation.md](secret-rotation.md), [ADR 0006](../adr/0006-operations.md).

Hosting is signed ([decision 7](../decisions/07-hosting.md)): Supabase Pro for the database, Fly.io for the API
([ADR 0008](../adr/0008-fly-deployment.md)). This runbook has a provider part (managed backups) and a
provider-neutral part (logical dump with `pg_dump`). Do both. Fly.io specifics: section "Fly.io" at the end.

## What to back up

| Data | Where | Backup |
|---|---|---|
| PostgreSQL schema `vybe` | Database | Provider backups (daily; point-in-time recovery strongly recommended) and a weekly logical dump |
| Deletion log (J4) | Export bucket, prefix `deletion-log/` | It is the backup of the deletion requests. Keep each object at least 90 days (longer than the oldest database backup). |
| Data exports (J4) | Export bucket, prefix `exports/` | No backup. Exports are temporary (24 h) and can be made again. |
| Database privileges | [`infra/db/grants.sql`](../../infra/db/grants.sql) | Git. Run the script after every restore. |
| Secrets | Platform secret store | Not in backups. Keep the list of names in [deploy-and-rollback.md](deploy-and-rollback.md). |
| Content (rulesets, catalog, quests) | Git | Git is the source; `seed` loads it again. |

Bucket lifecycle rules: a rule that deletes old exports must apply to the prefix `exports/` only. A rule for
`deletion-log/` deletes objects after 90 days or more, never earlier.

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

## Fly.io

The API on Fly.io keeps no data: no volumes, and the release machine keeps nothing. Only the database
(Supabase) and the export bucket (Supabase Storage) need backups. Images stay in `registry.fly.io` and can
be built again from git.

For "Restore production after data loss":

- Step 1, stop writes: `fly scale count 0 -a vybe-api`. The web app stays up; `/api` answers 502.
  Write down `fly scale show -a vybe-api` first, to scale back to the same count.
- Step 2: on Supabase, use "Restore to a new project" (Database → Backups) if the plan offers it. An
  in-place restore overwrites the damaged database: only with the approval of the incident lead.
- Steps 3–6 (grants, `migrate`, `replay-deletions`): run them from a trusted machine with
  `docker run --env-file <file>` and the image of the current release (`fly releases -a vybe-api --image`),
  as in the drill, but with the production bucket settings. Do not use `fly machine run` here: it starts
  the machine with the old connection strings of the app.
- Step 7: put the new connection strings in with `fly secrets import --stage -a vybe-api` (see the
  deploy runbook, one-time setup step 8). Then `fly secrets deploy -a vybe-api` and
  `fly scale count <count> -a vybe-api --region <region>`. Check `fly checks list -a vybe-api`.
- A new Supabase project has a new project ref: also change `Auth__Supabase__*`, `Storage__Exports__*`,
  and the web settings `VYBE_SUPABASE_URL` and `VYBE_SUPABASE_ANON_KEY` (`fly secrets set -a vybe-web`).
  Players must sign in again.
