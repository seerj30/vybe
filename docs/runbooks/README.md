# Runbooks

Operations procedures for the VYBE API (stream D). Decisions: [ADR 0006](../adr/0006-operations.md).
Hosting is signed ([decision 7](../decisions/07-hosting.md)): Supabase Pro and Fly.io ([ADR 0008](../adr/0008-fly-deployment.md)).
The runbooks are provider-neutral; the Fly.io steps are in their own sections.

| Runbook | Use it when |
|---|---|
| [deploy-and-rollback.md](deploy-and-rollback.md) | You release a new image, or a release is bad. Lists every setting. Fly.io one-time setup, first deploy, smoke test, scaling, cost. |
| [migrations.md](migrations.md) | You write, review or run a database migration. |
| [backup-restore.md](backup-restore.md) | You do the quarterly restore drill, or data is lost. |
| [incident-response.md](incident-response.md) | An alert fires or players report a problem. |
| [secret-rotation.md](secret-rotation.md) | A secret is due or exposed. |
| [load-test.md](load-test.md) | You measure capacity before a beta gate (k6 script in `infra/load/`). |
| [visual-baselines.md](visual-baselines.md) | You change the UI and the visual check fails, or you want to know which browsers run where. |
