# Runbooks

Operations procedures for the VYBE API (stream D). Decisions: [ADR 0006](../adr/0006-operations.md).
Hosting is not signed yet ([decision 7](../decisions/07-hosting.md)); the runbooks are provider-neutral
and name the provider steps where they differ.

| Runbook | Use it when |
|---|---|
| [deploy-and-rollback.md](deploy-and-rollback.md) | You release a new image, or a release is bad. Lists every setting. |
| [migrations.md](migrations.md) | You write, review or run a database migration. |
| [backup-restore.md](backup-restore.md) | You do the quarterly restore drill, or data is lost. |
| [incident-response.md](incident-response.md) | An alert fires or players report a problem. |
| [secret-rotation.md](secret-rotation.md) | A secret is due or exposed. |
| [load-test.md](load-test.md) | You measure capacity before a beta gate (k6 script in `infra/load/`). |
