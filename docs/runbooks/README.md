# Runbooks

Operations procedures for the VYBE API (stream D). Decisions: [ADR 0006](../adr/0006-operations.md).
Hosting ([decision 7](../decisions/07-hosting.md), changed on 2026-11-10): Supabase (Free, then Pro) and one Oracle
Cloud Always Free VM ([ADR 0009](../adr/0009-oracle-vm.md)). The Fly.io design ([ADR 0008](../adr/0008-fly-deployment.md)) is
superseded; its files and runbook sections were removed on 2026-11-10. The runbooks are provider-neutral; the hosting steps are in their own sections.

| Runbook | Use it when |
|---|---|
| [oracle-vm.md](oracle-vm.md) | You set up, deploy to, roll back, update or monitor the production VM (Oracle Cloud). |
| [deploy-and-rollback.md](deploy-and-rollback.md) | You release a new image, or a release is bad. Lists every setting. Generic deploy and rollback procedure. |
| [migrations.md](migrations.md) | You write, review or run a database migration. |
| [backup-restore.md](backup-restore.md) | You do the quarterly restore drill, or data is lost. Weekly encrypted dump on Supabase Free. |
| [incident-response.md](incident-response.md) | An alert fires or players report a problem. |
| [secret-rotation.md](secret-rotation.md) | A secret is due or exposed. |
| [load-test.md](load-test.md) | You measure capacity before a beta gate (k6 script in `infra/load/`). |
| [visual-baselines.md](visual-baselines.md) | You change the UI and the visual check fails, or you want to know which browsers run where. |
