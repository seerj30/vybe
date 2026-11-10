-- Read-only role for the weekly pg_dump (infra/oracle/backup.sh, docs/runbooks/backup-restore.md). The backup workflow
-- runs without a reviewer, so it must not have the owner credential: this role can read schema vybe and nothing more.
--
-- 1. As postgres (it has CREATEROLE), once:
--      CREATE ROLE vybe_backup LOGIN;
--      ALTER ROLE vybe_backup SET default_transaction_read_only = on;
--      \password vybe_backup
-- 2. As the owner role (the default privileges below are the owner's), once and after every restore:
--      PGSERVICE=vybe-owner psql -v ON_ERROR_STOP=1 -f infra/oracle/backup-grants.sql
--
-- pg_dump needs USAGE on the schema, SELECT on every table (it also takes ACCESS SHARE locks with it) and SELECT on
-- every sequence (the current value). Functions, triggers and types are read from the catalog. Schema vybe has no
-- row-level security, so no BYPASSRLS is needed.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vybe_backup') THEN
        RAISE EXCEPTION 'role vybe_backup does not exist; create it first (step 1 above)';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vybe_backup'
               AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls OR rolreplication)) THEN
        RAISE EXCEPTION 'role vybe_backup must have no attributes except LOGIN';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE n.nspname = 'vybe' AND pg_get_userbyid(c.relowner) = 'vybe_backup') THEN
        RAISE EXCEPTION 'role vybe_backup owns objects in schema vybe; it must own none';
    END IF;
END;
$$;

GRANT USAGE ON SCHEMA vybe TO vybe_backup;
GRANT SELECT ON ALL TABLES IN SCHEMA vybe TO vybe_backup;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA vybe TO vybe_backup;
-- Tables and sequences of later migrations (created by this owner role) get the same read access.
ALTER DEFAULT PRIVILEGES IN SCHEMA vybe GRANT SELECT ON TABLES TO vybe_backup;
ALTER DEFAULT PRIVILEGES IN SCHEMA vybe GRANT SELECT ON SEQUENCES TO vybe_backup;
