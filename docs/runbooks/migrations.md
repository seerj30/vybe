# Runbook: database migrations

- Owner: stream D (operations). Schema decisions: [ADR 0002](../adr/0002-database-schema.md).
- Related: [deploy-and-rollback.md](deploy-and-rollback.md), [backup-restore.md](backup-restore.md).

## Rules

1. The API never migrates at start-up. Migrations run only through the `migrate` command of the image,
   as a separate deploy step before the new image receives traffic.
2. Every migration is **expand-contract** (spec §13). A release adds columns, tables and indexes
   (expand). A later release removes what the code no longer uses (contract). The running (old) image
   must work with the new schema, because rollback never reverts the database.
3. The `migrate` step uses `ConnectionStrings:Migrations`: the owner role, through a direct connection or
   the session-mode pooler. Do not run DDL through the transaction pooler (port 6543 on Supabase).
4. The API connects with a role that owns no tables (ADR 0002 operations notes).
5. Never edit a migration that is applied in any shared environment. Add a new migration.

## Write a migration

1. Change the model in `services/api/src/Vybe.Data`.
2. Add the migration:
   ```bash
   cd services/api && dotnet tool restore && dotnet ef migrations add <Name> --project src/Vybe.Data
   ```
3. Regenerate the review script:
   ```bash
   cd services/api && dotnet ef migrations script --project src/Vybe.Data --output db/schema.sql
   ```
4. Run `dotnet test services/api`. The test `Migrations_ApplyToCleanDatabase_AndMatchTheModel` fails when
   the model has changes without a migration.
5. In the PR body, write "schema change" and say which step (expand or contract) the migration is.

## Review checklist (expand step)

- New columns are nullable, or have a database default. The old image does not write them.
- No `DROP`, no `RENAME` of a column or table that the old image reads or writes.
- No new `NOT NULL` without a default on an existing table.
- No new CHECK value list that rejects a value the old image writes.
- Large tables: create indexes with `CREATE INDEX CONCURRENTLY` in a migration with
  `migrationBuilder.Sql(..., suppressTransaction: true)`. A normal `CREATE INDEX` locks writes.
- Trigger and guard changes (ADR 0002 #6): the old image must still pass them.
- Long data backfills: do them in batches in a job, not in the migration transaction.

## Contract step

Remove a column or table only in a release after the release that stopped using it is on every
environment. Then the previous image (the rollback target) also does not use it.

## Run the migration step

```bash
docker run --rm \
  -e ConnectionStrings__Migrations="$VYBE_MIGRATIONS_CONNECTION" \
  <registry>/vybe-api:<sha> migrate
```

- The log lists the migrations that it applies, then "The database schema is up to date".
- Exit code 0 = success. Exit code 1 = failure; the log has the exception.
- The command is idempotent. Running it again applies nothing.
- EF Core applies each migration in a transaction. A failed migration leaves the earlier migrations of the
  run applied and the failed one not applied.

## Check the state

- `GET /health/ready` on an instance of the new image returns 503 while a migration of that image is
  not applied. The log of the instance says "Not ready: N migrations are not applied".
- The applied migrations are in `vybe.__ef_migrations_history`:
  ```sql
  SELECT migration_id FROM vybe.__ef_migrations_history ORDER BY migration_id;
  ```

## A migration failed

1. Do not deploy the new image. The old image continues to serve traffic.
2. Read the exception in the log of the `migrate` step.
3. Lock timeout or a long lock wait: run the step again at a time with less traffic.
4. Data does not satisfy a new constraint: fix the data or the migration in a new commit, then run the
   release again.
5. If the database is in a state that the old image cannot use: follow
   [incident-response.md](incident-response.md) (SEV-1) and, if data is damaged,
   [backup-restore.md](backup-restore.md).
