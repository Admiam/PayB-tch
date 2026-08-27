using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paybitch.Infrastructure.Migrations
{
    /// <summary>
    /// SQL-only schema invariants that EF's fluent model cannot express:
    ///  - case-insensitive category uniqueness (lower(name)) — §3.9 / §1.3
    ///  - the deferred split-sum constraint trigger (SUM(share_minor) = amount_minor) — §1.5 / D3
    ///  - in-flight export dedup on md5(params) — E4 §4.3
    /// The member-in-group and ≥1-owner invariants (§1.5) are enforced in the handlers.
    /// </summary>
    public partial class SchemaHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Category names are unique case-insensitively (EF emitted them on plain name).
            migrationBuilder.Sql("DROP INDEX IF EXISTS uq_categories_global;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS uq_categories_group;");
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX uq_categories_global ON categories (lower(name))
                    WHERE group_id IS NULL AND deleted_at IS NULL;");
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX uq_categories_group ON categories (group_id, lower(name))
                    WHERE group_id IS NOT NULL AND deleted_at IS NULL;");

            // 2. Deferred split-sum invariant (D3): the resolved splits must sum to the expense
            //    amount at COMMIT. FOR UPDATE on the expense row serializes interleaved split
            //    edits (closes the §1.5 TOCTOU). Fires on expense_splits mutations; a cascade
            //    delete of the parent expense leaves amount_minor NULL and is skipped.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION check_expense_split_sum() RETURNS trigger AS $$
                DECLARE
                    v_expense_id uuid := COALESCE(NEW.expense_id, OLD.expense_id);
                    v_amount bigint;
                    v_sum bigint;
                BEGIN
                    SELECT amount_minor INTO v_amount FROM expenses WHERE id = v_expense_id FOR UPDATE;
                    IF v_amount IS NULL THEN
                        RETURN NULL; -- parent expense gone (hard cascade); nothing to check
                    END IF;
                    SELECT COALESCE(SUM(share_minor), 0) INTO v_sum
                        FROM expense_splits WHERE expense_id = v_expense_id;
                    IF v_sum <> v_amount THEN
                        RAISE EXCEPTION 'split sum % <> expense amount % for expense %',
                            v_sum, v_amount, v_expense_id USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;");
            migrationBuilder.Sql(@"
                CREATE CONSTRAINT TRIGGER trg_expense_split_sum
                    AFTER INSERT OR UPDATE OR DELETE ON expense_splits
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION check_expense_split_sum();");

            // 3. One in-flight export per (requester, kind, params) — collapses double-fire (E4).
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX uq_export_results_inflight
                    ON export_results (requested_by, kind, md5(params::text))
                    WHERE status = 'pending';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS uq_export_results_inflight;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_expense_split_sum ON expense_splits;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS check_expense_split_sum();");
            migrationBuilder.Sql("DROP INDEX IF EXISTS uq_categories_global;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS uq_categories_group;");
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX uq_categories_global ON categories (name)
                    WHERE group_id IS NULL AND deleted_at IS NULL;");
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX uq_categories_group ON categories (group_id, name)
                    WHERE group_id IS NOT NULL AND deleted_at IS NULL;");
        }
    }
}
