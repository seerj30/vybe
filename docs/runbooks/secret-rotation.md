# Runbook: secret rotation

- Owner: stream D (operations).
- Related: [deploy-and-rollback.md](deploy-and-rollback.md) (list of settings), [incident-response.md](incident-response.md).

## Rules

- Secrets live only in the platform secret store (spec §13), never in the repository, in images, in logs
  or in PR text. Images contain no secrets; settings come from environment variables at run time.
- Rotate on a schedule (below) and at once when a secret is exposed or a person with access leaves.
- After each rotation, write the date and the person in the record at the end of this file (never the value).
- A leaked secret that is in use is a SEV-1 incident.

## Schedule

| Secret | Schedule | Effect of a rotation on players |
|---|---|---|
| Database passwords (`ConnectionStrings__Vybe`, `ConnectionStrings__Migrations`) | 6 months | None (rolling restart) |
| Storage S3 access keys (`Storage__Exports__*`) | 6 months | None |
| `Challenges__InviteSecret` | Only when exposed | All open invite links stop working (ADR 0004) |
| `Sentry__Dsn` | Only when abused | None |
| OTLP collector credentials (`OTEL_EXPORTER_OTLP_HEADERS`) | 12 months | None |
| `Auth__Supabase__ApiKey` (publishable key) | Not secret; rotate with Supabase key rotation | None if both keys overlap |
| Fly.io deploy tokens (GitHub secrets `FLY_API_TOKEN_API`, `FLY_API_TOKEN_WEB`) | 12 months (token expiry) | None |

## Database password

Use two login roles per purpose (for example `vybe_api_a` and `vybe_api_b`) so that the old and the new
password are valid at the same time.

1. Set a new password on the role that is not in use:
   `ALTER ROLE vybe_api_b WITH PASSWORD '<new>';` (run from a trusted session; do not keep it in shell history).
2. Put the new connection string in the secret store as `ConnectionStrings__Vybe`.
3. Do a rolling restart of the API. Check `/health/ready` on the new instances.
4. Disable the old role's login: `ALTER ROLE vybe_api_a WITH NOLOGIN;`
5. Same procedure for the migrations role; the next `migrate` step uses the new value.

On Supabase with Supavisor, the user name in the connection string has the form `role.project-ref`.

## Storage S3 access keys

1. Create a new S3 access key pair in the storage provider (Supabase: Storage → S3 access keys).
   Give it access to the export bucket only, if the provider supports it.
2. Update `Storage__Exports__AccessKeyId` and `Storage__Exports__SecretAccessKey` in the secret store.
3. Rolling restart. Request a data export with a test account and download it.
4. Revoke the old key pair.

Signed export URLs from the old key stop working when the old key is revoked. They are valid for at most
10 minutes, so wait 10 minutes after step 3 before step 4.

## Invite secret (`Challenges__InviteSecret`)

There is no overlap period: the API knows one secret. A rotation invalidates every open invite link.

1. Make a new secret: `openssl rand -base64 32`.
2. Put it in the secret store. Rolling restart.
3. Open challenges stay in the database; their links answer as unknown. Players can create new
   challenges. Tell players if many open challenges were affected.

## Sentry DSN and OTLP credentials

1. Create a new DSN (Sentry project settings → Client keys) or a new collector token.
2. Update the secret store. Rolling restart.
3. Disable the old DSN or token.

## Supabase keys

The API uses the publishable (anon) key only, to call `GET {issuer}/user` during account linking. It does
not use the service role key. When Supabase keys rotate, update `Auth__Supabase__ApiKey`, then restart.
The JWT signing keys are read from the issuer metadata; a key rotation in Supabase Auth needs no API change.

## Rotation record

| Date | Secret (name only) | Reason | Done by |
|---|---|---|---|
| — | — | — | — |

## Fly.io

- Secrets of an app: `fly secrets list -a <app>` shows names and digests only. Put a new value in with
  `fly secrets import --stage -a <app>` (stdin; see [deploy-and-rollback.md](deploy-and-rollback.md),
  one-time setup step 8). Then run the Deploy workflow, or `fly secrets deploy -a <app>`. The workflow uses
  the bluegreen strategy, so there is no gap; `fly secrets deploy` restarts the machines and, with one
  machine, the API is down for some seconds.
- `ConnectionStrings__Migrations` is **not** a Fly secret: it is the GitHub environment secret
  `VYBE_MIGRATIONS_CONNECTION_STRING`, used only by the release step of the deploy workflow (ADR 0008).
  Rotate the password like the other database passwords, then
  `gh secret set VYBE_MIGRATIONS_CONNECTION_STRING --env production` (paste at the prompt).
- Deploy tokens: `fly tokens create deploy -a <app> -x 8760h | gh secret set FLY_API_TOKEN_API --env production`
  (or `FLY_API_TOKEN_WEB`), run the Deploy workflow, then revoke the old token: `fly tokens list -a <app>` and
  `fly tokens revoke <id>`.
- An exposed deploy token: revoke it at once (SEV-1 if a deploy that nobody approved is possible), then
  check `fly releases -a <app>` for deploys that nobody approved.
