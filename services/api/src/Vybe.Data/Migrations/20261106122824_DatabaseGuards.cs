using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vybe.Data.Migrations
{
    /// <summary>
    /// Hand-written guards that EF Core cannot model (spec v1.3 §4, ADR 0002):
    /// append-only ledger and audit log, immutable rulesets and finished sessions,
    /// automatic concurrency-token increments, and no access for PUBLIC or the Supabase API roles.
    /// </summary>
    public partial class DatabaseGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- Append-only. The only exception: the audit-log retention job (spec §11, 90 days)
                -- deletes rows older than 90 days inside a transaction that ran
                -- set_config('vybe.retention_delete', 'on', true). The age check makes a leaked
                -- session-level setting (e.g. on a pooled connection) unable to delete recent rows.
                CREATE FUNCTION vybe.reject_append_only_change() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' AND TG_LEVEL = 'ROW' AND TG_TABLE_NAME = 'admin_audit_logs'
                       AND current_setting('vybe.retention_delete', true) = 'on'
                       AND OLD.created_at < now() - interval '90 days' THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'table %.% is append-only (% rejected)', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'integrity_constraint_violation';
                END;
                $$;

                CREATE TRIGGER wallet_transactions_append_only
                    BEFORE UPDATE OR DELETE ON vybe.wallet_transactions
                    FOR EACH ROW EXECUTE FUNCTION vybe.reject_append_only_change();
                CREATE TRIGGER wallet_transactions_no_truncate
                    BEFORE TRUNCATE ON vybe.wallet_transactions
                    FOR EACH STATEMENT EXECUTE FUNCTION vybe.reject_append_only_change();

                CREATE TRIGGER admin_audit_logs_append_only
                    BEFORE UPDATE OR DELETE ON vybe.admin_audit_logs
                    FOR EACH ROW EXECUTE FUNCTION vybe.reject_append_only_change();
                CREATE TRIGGER admin_audit_logs_no_truncate
                    BEFORE TRUNCATE ON vybe.admin_audit_logs
                    FOR EACH STATEMENT EXECUTE FUNCTION vybe.reject_append_only_change();

                -- Rulesets are immutable; only retirement may be recorded.
                CREATE FUNCTION vybe.guard_game_ruleset() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'game ruleset % cannot be deleted', OLD.id
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    IF (to_jsonb(NEW) - 'retired_at') IS DISTINCT FROM (to_jsonb(OLD) - 'retired_at') THEN
                        RAISE EXCEPTION 'game ruleset % is immutable; only retired_at may change', OLD.id
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    IF OLD.retired_at IS NOT NULL AND NEW.retired_at IS DISTINCT FROM OLD.retired_at THEN
                        RAISE EXCEPTION 'game ruleset % is already retired', OLD.id
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER game_rulesets_immutable
                    BEFORE UPDATE OR DELETE ON vybe.game_rulesets
                    FOR EACH ROW EXECUTE FUNCTION vybe.guard_game_ruleset();

                -- Finished sessions: the outcome columns are immutable. A fixed column list (not the
                -- whole row) lets future expand/contract migrations backfill new columns; a new
                -- column that must also be immutable is added to this list in its migration.
                CREATE FUNCTION vybe.guard_finished_game_session() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.status IN ('verified', 'rejected', 'expired')
                       AND ROW(NEW.player_id, NEW.game_id, NEW.ruleset_id, NEW.seed, NEW.status, NEW.created_at,
                               NEW.start_deadline, NEW.started_at, NEW.submit_deadline, NEW.submitted_at,
                               NEW.verified_at, NEW.reward_day, NEW.event_count, NEW.score, NEW.reward_coins,
                               NEW.reward_xp, NEW.input_trace_hash, NEW.rejection_reason, NEW.challenge_id)
                           IS DISTINCT FROM
                           ROW(OLD.player_id, OLD.game_id, OLD.ruleset_id, OLD.seed, OLD.status, OLD.created_at,
                               OLD.start_deadline, OLD.started_at, OLD.submit_deadline, OLD.submitted_at,
                               OLD.verified_at, OLD.reward_day, OLD.event_count, OLD.score, OLD.reward_coins,
                               OLD.reward_xp, OLD.input_trace_hash, OLD.rejection_reason, OLD.challenge_id) THEN
                        RAISE EXCEPTION 'game session % is %; its outcome is immutable', OLD.id, OLD.status
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER game_sessions_finished_immutable
                    BEFORE UPDATE ON vybe.game_sessions
                    FOR EACH ROW EXECUTE FUNCTION vybe.guard_finished_game_session();

                -- Concurrency tokens (spec §4 invariant 5): every UPDATE increments them, so a
                -- compare-and-swap on version/revision cannot pass twice, even if code forgets to bump.
                -- Trigger names sort before the guards, so the guards see the incremented value.
                CREATE FUNCTION vybe.increment_version() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.version := OLD.version + 1;
                    RETURN NEW;
                END;
                $$;

                CREATE FUNCTION vybe.increment_revision() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.revision := OLD.revision + 1;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER game_sessions_a_increment_version
                    BEFORE UPDATE ON vybe.game_sessions
                    FOR EACH ROW EXECUTE FUNCTION vybe.increment_version();
                CREATE TRIGGER challenges_a_increment_version
                    BEFORE UPDATE ON vybe.challenges
                    FOR EACH ROW EXECUTE FUNCTION vybe.increment_version();
                CREATE TRIGGER wallets_a_increment_revision
                    BEFORE UPDATE ON vybe.wallets
                    FOR EACH ROW EXECUTE FUNCTION vybe.increment_revision();
                CREATE TRIGGER avatar_configs_a_increment_revision
                    BEFORE UPDATE ON vybe.avatar_configs
                    FOR EACH ROW EXECUTE FUNCTION vybe.increment_revision();

                -- Foreign keys EF cannot model (their target columns are nullable).
                -- The recipient session of a challenge carries that challenge's id.
                ALTER TABLE vybe.game_sessions
                    ADD CONSTRAINT ak_game_sessions_id_challenge_id UNIQUE (id, challenge_id);
                ALTER TABLE vybe.challenges
                    ADD CONSTRAINT fk_challenges_recipient_session_challenge
                    FOREIGN KEY (recipient_session_id, id) REFERENCES vybe.game_sessions (id, challenge_id);
                -- A purchase grant points at the buyer's own ledger row for the same item.
                ALTER TABLE vybe.wallet_transactions
                    ADD CONSTRAINT ak_wallet_transactions_id_player_id_reference_id UNIQUE (id, player_id, reference_id);
                ALTER TABLE vybe.player_inventory
                    ADD CONSTRAINT fk_player_inventory_purchase_transaction
                    FOREIGN KEY (source_transaction_id, player_id, item_id)
                    REFERENCES vybe.wallet_transactions (id, player_id, reference_id);

                -- A recipient run (challenge_id set) cannot be the creator run of a challenge.
                CREATE FUNCTION vybe.guard_challenge_creator_session() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM vybe.game_sessions
                               WHERE id = NEW.creator_session_id AND challenge_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'session % is a recipient run and cannot create a challenge', NEW.creator_session_id
                            USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER challenges_creator_session_not_recipient_run
                    BEFORE INSERT OR UPDATE OF creator_session_id ON vybe.challenges
                    FOR EACH ROW EXECUTE FUNCTION vybe.guard_challenge_creator_session();

                REVOKE ALL ON FUNCTION vybe.guard_challenge_creator_session() FROM PUBLIC;
                REVOKE ALL ON FUNCTION vybe.increment_version() FROM PUBLIC;
                REVOKE ALL ON FUNCTION vybe.increment_revision() FROM PUBLIC;
                REVOKE ALL ON FUNCTION vybe.reject_append_only_change() FROM PUBLIC;
                REVOKE ALL ON FUNCTION vybe.guard_game_ruleset() FROM PUBLIC;
                REVOKE ALL ON FUNCTION vybe.guard_finished_game_session() FROM PUBLIC;

                -- Only the API's own role uses this schema. On Supabase, the Data API roles
                -- (anon, authenticated) must never reach it.
                REVOKE ALL ON SCHEMA vybe FROM PUBLIC;
                REVOKE ALL ON ALL TABLES IN SCHEMA vybe FROM PUBLIC;
                ALTER DEFAULT PRIVILEGES IN SCHEMA vybe REVOKE ALL ON TABLES FROM PUBLIC;
                DO $$
                DECLARE
                    api_role text;
                BEGIN
                    FOREACH api_role IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = api_role) THEN
                            EXECUTE format('REVOKE ALL ON SCHEMA vybe FROM %I', api_role);
                            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA vybe FROM %I', api_role);
                            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA vybe REVOKE ALL ON TABLES FROM %I', api_role);
                        END IF;
                    END LOOP;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER challenges_creator_session_not_recipient_run ON vybe.challenges;
                DROP FUNCTION vybe.guard_challenge_creator_session();
                ALTER TABLE vybe.player_inventory DROP CONSTRAINT fk_player_inventory_purchase_transaction;
                ALTER TABLE vybe.wallet_transactions DROP CONSTRAINT ak_wallet_transactions_id_player_id_reference_id;
                ALTER TABLE vybe.challenges DROP CONSTRAINT fk_challenges_recipient_session_challenge;
                ALTER TABLE vybe.game_sessions DROP CONSTRAINT ak_game_sessions_id_challenge_id;
                DROP TRIGGER avatar_configs_a_increment_revision ON vybe.avatar_configs;
                DROP TRIGGER wallets_a_increment_revision ON vybe.wallets;
                DROP TRIGGER challenges_a_increment_version ON vybe.challenges;
                DROP TRIGGER game_sessions_a_increment_version ON vybe.game_sessions;
                DROP FUNCTION vybe.increment_revision();
                DROP FUNCTION vybe.increment_version();
                DROP TRIGGER game_sessions_finished_immutable ON vybe.game_sessions;
                DROP TRIGGER game_rulesets_immutable ON vybe.game_rulesets;
                DROP TRIGGER admin_audit_logs_no_truncate ON vybe.admin_audit_logs;
                DROP TRIGGER admin_audit_logs_append_only ON vybe.admin_audit_logs;
                DROP TRIGGER wallet_transactions_no_truncate ON vybe.wallet_transactions;
                DROP TRIGGER wallet_transactions_append_only ON vybe.wallet_transactions;
                DROP FUNCTION vybe.guard_finished_game_session();
                DROP FUNCTION vybe.guard_game_ruleset();
                DROP FUNCTION vybe.reject_append_only_change();
                """);
        }
    }
}
