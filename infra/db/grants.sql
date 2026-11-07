-- VYBE database privileges (ADR 0002 operations notes, ADR 0006).
-- Run as the owner role (the migrations role) after the first migration and after every restore.
-- Idempotent. A pg_dump/pg_restore with --no-privileges does not keep these, so always run it after a restore.
--
--   psql "service=vybe-owner" -v ON_ERROR_STOP=1 -v api_role=vybe_api -f infra/db/grants.sql
--
-- The API role must exist (CREATE ROLE vybe_api LOGIN ...) and must own no tables.

\if :{?api_role}
\else
  \set api_role vybe_api
\endif

SELECT set_config('vybe.grants_api_role', :'api_role', false);

DO $$
DECLARE
    api text := current_setting('vybe.grants_api_role');
    data_api_role text;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = api) THEN
        RAISE EXCEPTION 'role % does not exist; create it first', api;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'vybe' AND tableowner = api) THEN
        RAISE EXCEPTION 'role % owns tables in schema vybe; the API role must own none', api;
    END IF;

    -- Nobody except the owner and the API role. On Supabase, the Data API roles never reach the schema.
    REVOKE ALL ON SCHEMA vybe FROM PUBLIC;
    REVOKE ALL ON ALL TABLES IN SCHEMA vybe FROM PUBLIC;
    REVOKE ALL ON ALL SEQUENCES IN SCHEMA vybe FROM PUBLIC;
    REVOKE ALL ON ALL FUNCTIONS IN SCHEMA vybe FROM PUBLIC;
    ALTER DEFAULT PRIVILEGES IN SCHEMA vybe REVOKE ALL ON TABLES FROM PUBLIC;
    FOREACH data_api_role IN ARRAY ARRAY['anon', 'authenticated'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = data_api_role) THEN
            EXECUTE format('REVOKE ALL ON SCHEMA vybe FROM %I', data_api_role);
            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA vybe FROM %I', data_api_role);
            EXECUTE format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA vybe FROM %I', data_api_role);
            EXECUTE format('REVOKE ALL ON ALL FUNCTIONS IN SCHEMA vybe FROM %I', data_api_role);
            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA vybe REVOKE ALL ON TABLES FROM %I', data_api_role);
        END IF;
    END LOOP;

    -- The API role: data access only (no CREATE, no TRUNCATE). The database guards still apply to it.
    EXECUTE format('GRANT USAGE ON SCHEMA vybe TO %I', api);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA vybe TO %I', api);
    EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA vybe TO %I', api);
    -- Tables that later migrations create (as this owner role) get the same grants.
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA vybe GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', api);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA vybe GRANT USAGE, SELECT ON SEQUENCES TO %I', api);
END;
$$;
