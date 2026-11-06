# Runbook: backup and restore drill

- Owner: stream D (operations); the on-call primary does the quarterly drill.
- Spec §13: "backups and quarterly restore drills". Spec §12: alert on backup failure.
- Related: [incident-response.md](incident-response.md), [migrations.md](migrations.md).

Hosting is not signed yet ([decision 7](../decisions/07-hosting.md)). This runbook has a provider part
(managed backups) and a provider-neutral part (logical dump with `pg_dump`). Do both.

## What to back up

| Data | Where | Backup |
|---|---|---|
| PostgreSQL schema `vybe` | Database | Provider backups (daily + point-in-time recovery if bought) and a weekly logical dump |
| Data exports (J4) | Object storage bucket `Storage:Exports:Bucket` | No backup. Exports are temporary (24 h) and can be made again. |
| Secrets | Platform secret store | Not in backups. Keep the list of names in [deploy-and-rollback.md](deploy-and-rollback.md). |
| Content (rulesets, catalog, quests) | Git | Git is the source; `seed` loads it again. |

Supabase Pro: daily backups are included; the retention depends on the plan. Point-in-time recovery is an
add-on. Check the current plan on the decision date and write the retention here: **retention: TBD**.

## Weekly logical dump (provider-neutral)

Run from a trusted machine or a scheduled job with network access to the database. Use the owner
connection through a direct or session-mode connection (not the transaction pooler).

```bash
pg_dump "$VYBE_MIGRATIONS_CONNECTION" \
  --schema=vybe --format=custom --no-owner --no-privileges \
  --file="vybe-$(date -u +%Y%m%dT%H%MZ).dump"
```

- Encrypt the file before upload (for example `age` or the storage provider's server-side encryption).
- Store it in a bucket in another account or provider than the database, with a lifecycle rule
  (for example: delete after 35 days). The dump has personal data: same access rules as production.
- A failed dump must alert the on-call people (spec §12 "backup failure").

## Quarterly restore drill

Goal: prove that a backup restores to a working API, and measure the time (RTO) and the data loss (RPO).

1. Create an empty PostgreSQL 17 database that is isolated from production (a new Supabase project, or
   `docker run -d --name vybe-restore -e POSTGRES_PASSWORD=<local> -p 127.0.0.1:55434:5432 postgres:17.6-alpine`).
2. Write down the start time.
3. Restore the newest backup:
   - Provider backup: use the provider's "restore to new project" function.
   - Logical dump:
     ```bash
     createdb -h 127.0.0.1 -p 55434 -U postgres vybe
     pg_restore -h 127.0.0.1 -p 55434 -U postgres -d vybe --no-owner --exit-on-error vybe-<time>.dump
     ```
4. Run the image of the current production release against the restored database:
   `migrate` (it must apply nothing, or only migrations newer than the backup), then the API with
   `ConnectionStrings__Vybe` set to the restored database.
5. Check:
   - `GET /health/ready` returns 200.
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
6. Write down the end time. RTO = end − start. RPO = start − `newest_ledger_row`.
7. Delete the restored database and every copy of the dump on the drill machine.
8. Record the drill in the table below. Open an issue for each problem.

## Drill record

| Date | Backup used | RTO | RPO | Result | Done by |
|---|---|---|---|---|---|
| — | — | — | — | No drill yet | — |

## Restore production after data loss

Do this only as part of an incident ([incident-response.md](incident-response.md)), with the incident lead.

1. Stop writes: scale the API to zero, or set the edge to return 503 for `/api`.
2. Restore to a **new** database (never over the damaged one); keep the damaged one for analysis.
3. Run steps 4–5 of the drill against the new database.
4. Change `ConnectionStrings__Vybe` and `ConnectionStrings__Migrations` to the new database
   ([secret-rotation.md](secret-rotation.md)), then start the API.
5. Tell players about the lost time window, if any (incident communication).
