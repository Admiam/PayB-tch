using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Paybitch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "blob_deletions",
                columns: table => new
                {
                    storage_key = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blob_deletions", x => x.storage_key);
                    table.CheckConstraint("ck_blob_deletions_reason", "reason IN ('attachment_purged','avatar_replaced','gdpr_erase','orphan')");
                });

            migrationBuilder.CreateTable(
                name: "change_log_watermark",
                columns: table => new
                {
                    one = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    pruned_through_seq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    pruned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_log_watermark", x => x.one);
                    table.CheckConstraint("ck_change_log_watermark_one", "one");
                });

            migrationBuilder.CreateTable(
                name: "currencies",
                columns: table => new
                {
                    code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    minor_units = table.Column<short>(type: "smallint", nullable: false),
                    symbol = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_currencies", x => x.code);
                    table.CheckConstraint("ck_currencies_minor_units", "minor_units BETWEEN 0 AND 4");
                });

            migrationBuilder.CreateTable(
                name: "email_suppressions",
                columns: table => new
                {
                    email = table.Column<string>(type: "citext", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_suppressions", x => x.email);
                    table.CheckConstraint("ck_email_suppressions_reason", "reason IN ('bounce','complaint','manual')");
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "queued"),
                    run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    priority = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)100),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    max_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 8),
                    locked_by = table.Column<string>(type: "text", nullable: true),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    dedupe_key = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_status", "status IN ('queued','running','succeeded','failed','dead')");
                });

            migrationBuilder.CreateTable(
                name: "notification_cursor",
                columns: table => new
                {
                    worker = table.Column<string>(type: "text", nullable: false),
                    last_activity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_cursor", x => x.worker);
                });

            migrationBuilder.CreateTable(
                name: "rate_limit_counters",
                columns: table => new
                {
                    partition_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_limit_counters", x => new { x.partition_hash, x.window_start });
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: true),
                    default_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false, defaultValue: "CZK"),
                    locale = table.Column<string>(type: "text", nullable: false, defaultValue: "cs"),
                    avatar_url = table.Column<string>(type: "text", nullable: true),
                    token_epoch = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    email_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    digest_opt_in = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_users_currencies_default_currency",
                        column: x => x.default_currency,
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "auth_identities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    apple_refresh_token_enc = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_identities", x => x.id);
                    table.ForeignKey(
                        name: "fk_auth_identities_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    apns_token = table.Column<string>(type: "text", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false, defaultValue: "ios"),
                    kind = table.Column<string>(type: "text", nullable: false, defaultValue: "apns"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                    table.CheckConstraint("ck_devices_kind", "kind IN ('apns','apns_live_activity')");
                    table.ForeignKey(
                        name: "fk_devices_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "email_login_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: false),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_login_tokens", x => x.id);
                    table.CheckConstraint("ck_email_login_tokens_purpose", "purpose IN ('login','link','verify_change')");
                    table.ForeignKey(
                        name: "fk_email_login_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    default_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    icon_symbol = table.Column<string>(type: "text", nullable: true),
                    image_url = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_groups", x => x.id);
                    table.CheckConstraint("ck_groups_name_length", "length(name) BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "fk_groups_currencies_default_currency",
                        column: x => x.default_currency,
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_groups_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replaced_by = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_refresh_tokens_replaced_by",
                        column: x => x.replaced_by,
                        principalTable: "refresh_tokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "activity_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user = table.Column<Guid>(type: "uuid", nullable: true),
                    verb = table.Column<string>(type: "text", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activity_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_activity_log_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_activity_log_users_actor_user",
                        column: x => x.actor_user,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    icon_symbol = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.ForeignKey(
                        name: "fk_categories_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "change_log",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.SerialColumn),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_delete = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_log", x => x.seq);
                    table.CheckConstraint("ck_change_log_entity_type", "entity_type IN ('group','member','expense','settlement','category','access','recurring_rule','comment')");
                    table.ForeignKey(
                        name: "fk_change_log_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "export_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    @params = table.Column<string>(name: "params", type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    storage_key = table.Column<string>(type: "text", nullable: true),
                    byte_size = table.Column<long>(type: "bigint", nullable: true),
                    content_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_export_results", x => x.id);
                    table.CheckConstraint("ck_export_results_content_type", "content_type IN ('text/csv','application/pdf','application/json')");
                    table.CheckConstraint("ck_export_results_kind", "kind IN ('csv','pdf','gdpr')");
                    table.CheckConstraint("ck_export_results_status", "status IN ('pending','ready','failed','expired')");
                    table.ForeignKey(
                        name: "fk_export_results_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_export_results_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false, defaultValue: "member"),
                    icon_symbol = table.Column<string>(type: "text", nullable: true),
                    image_url = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    former_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_members", x => x.id);
                    table.CheckConstraint("ck_group_members_ghost_role", "user_id IS NOT NULL OR role = 'member'");
                    table.CheckConstraint("ck_group_members_role", "role IN ('owner','admin','member')");
                    table.ForeignKey(
                        name: "fk_group_members_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_group_members_users_former_user_id",
                        column: x => x.former_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_group_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "notification_prefs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_prefs", x => x.id);
                    table.CheckConstraint("ck_notification_prefs_channel", "channel IN ('push','email')");
                    table.ForeignKey(
                        name: "fk_notification_prefs_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notification_prefs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: true),
                    member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invited_by = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invites", x => x.id);
                    table.ForeignKey(
                        name: "fk_invites_group_members_member_id",
                        column: x => x.member_id,
                        principalTable: "group_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_invites_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_invites_users_invited_by",
                        column: x => x.invited_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "recurring_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    paid_by = table.Column<Guid>(type: "uuid", nullable: false),
                    split_type = table.Column<string>(type: "text", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    icon_symbol = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    freq = table.Column<string>(type: "text", nullable: false),
                    interval = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    by_month_day = table.Column<int>(type: "integer", nullable: true),
                    by_weekday = table.Column<int>(type: "integer", nullable: true),
                    timezone = table.Column<string>(type: "text", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: true),
                    remaining_count = table.Column<int>(type: "integer", nullable: true),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "active"),
                    pause_reason = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recurring_rules", x => x.id);
                    table.CheckConstraint("ck_recurring_rules_amount_positive", "amount_minor > 0");
                    table.CheckConstraint("ck_recurring_rules_by_month_day", "by_month_day IS NULL OR by_month_day BETWEEN 1 AND 31");
                    table.CheckConstraint("ck_recurring_rules_by_weekday", "by_weekday IS NULL OR by_weekday BETWEEN 1 AND 7");
                    table.CheckConstraint("ck_recurring_rules_end_bound_xor", "NOT (ends_on IS NOT NULL AND remaining_count IS NOT NULL)");
                    table.CheckConstraint("ck_recurring_rules_ended_next_run", "(status = 'ended') = (next_run_at IS NULL)");
                    table.CheckConstraint("ck_recurring_rules_ends_after_starts", "ends_on IS NULL OR ends_on >= starts_on");
                    table.CheckConstraint("ck_recurring_rules_freq", "freq IN ('weekly','monthly','yearly')");
                    table.CheckConstraint("ck_recurring_rules_interval", "\"interval\" BETWEEN 1 AND 60");
                    table.CheckConstraint("ck_recurring_rules_monthly_shape", "freq <> 'monthly' OR (by_month_day IS NOT NULL AND by_weekday IS NULL)");
                    table.CheckConstraint("ck_recurring_rules_notes_length", "notes IS NULL OR length(notes) <= 2000");
                    table.CheckConstraint("ck_recurring_rules_pause_reason", "pause_reason IN ('user','member_removed','payer_removed','backlog_exceeded','group_archived')");
                    table.CheckConstraint("ck_recurring_rules_remaining_count", "remaining_count IS NULL OR remaining_count >= 0");
                    table.CheckConstraint("ck_recurring_rules_status", "status IN ('active','paused','ended')");
                    table.CheckConstraint("ck_recurring_rules_title_length", "length(title) BETWEEN 1 AND 140");
                    table.CheckConstraint("ck_recurring_rules_weekly_shape", "freq <> 'weekly' OR (by_weekday IS NOT NULL AND by_month_day IS NULL)");
                    table.CheckConstraint("ck_recurring_rules_yearly_shape", "freq <> 'yearly' OR (by_weekday IS NULL AND by_month_day IS NULL)");
                    table.ForeignKey(
                        name: "fk_recurring_rules_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_recurring_rules_currencies_currency",
                        column: x => x.currency,
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recurring_rules_group_members_paid_by",
                        column: x => x.paid_by,
                        principalTable: "group_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recurring_rules_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_recurring_rules_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "settlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: true),
                    from_member = table.Column<Guid>(type: "uuid", nullable: false),
                    to_member = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    method = table.Column<string>(type: "text", nullable: true),
                    settled_on = table.Column<DateOnly>(type: "date", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settlements", x => x.id);
                    table.CheckConstraint("ck_settlements_amount_positive", "amount_minor > 0");
                    table.CheckConstraint("ck_settlements_distinct_members", "from_member <> to_member");
                    table.CheckConstraint("ck_settlements_notes_length", "notes IS NULL OR length(notes) <= 2000");
                    table.ForeignKey(
                        name: "fk_settlements_currencies_currency",
                        column: x => x.currency,
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_settlements_group_members_from_member",
                        column: x => x.from_member,
                        principalTable: "group_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_settlements_group_members_to_member",
                        column: x => x.to_member,
                        principalTable: "group_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_settlements_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_settlements_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "expenses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    paid_by = table.Column<Guid>(type: "uuid", nullable: false),
                    split_type = table.Column<string>(type: "text", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    icon_symbol = table.Column<string>(type: "text", nullable: true),
                    expense_date = table.Column<DateOnly>(type: "date", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    recurring_rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expenses", x => x.id);
                    table.CheckConstraint("ck_expenses_amount_positive", "amount_minor > 0");
                    table.CheckConstraint("ck_expenses_date_min", "expense_date >= DATE '2000-01-01'");
                    table.CheckConstraint("ck_expenses_notes_length", "notes IS NULL OR length(notes) <= 2000");
                    table.CheckConstraint("ck_expenses_split_type", "split_type IN ('equal','exact','shares','percentage')");
                    table.CheckConstraint("ck_expenses_title_length", "length(title) BETWEEN 1 AND 140");
                    table.ForeignKey(
                        name: "fk_expenses_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_expenses_currencies_currency",
                        column: x => x.currency,
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_expenses_group_members_paid_by",
                        column: x => x.paid_by,
                        principalTable: "group_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_expenses_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_expenses_recurring_rules_recurring_rule_id",
                        column: x => x.recurring_rule_id,
                        principalTable: "recurring_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_expenses_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "recurring_rule_splits",
                columns: table => new
                {
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    weight = table.Column<int>(type: "integer", nullable: true),
                    basis_points = table.Column<int>(type: "integer", nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recurring_rule_splits", x => new { x.rule_id, x.group_member_id });
                    table.ForeignKey(
                        name: "fk_recurring_rule_splits_group_members_group_member_id",
                        column: x => x.group_member_id,
                        principalTable: "group_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recurring_rule_splits_recurring_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "recurring_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expense_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comments", x => x.id);
                    table.CheckConstraint("ck_comments_body_length", "length(body) BETWEEN 1 AND 2000");
                    table.ForeignKey(
                        name: "fk_comments_expenses_expense_id",
                        column: x => x.expense_id,
                        principalTable: "expenses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_comments_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_comments_users_author_user",
                        column: x => x.author_user,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "expense_splits",
                columns: table => new
                {
                    expense_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_minor = table.Column<long>(type: "bigint", nullable: false),
                    weight = table.Column<int>(type: "integer", nullable: true),
                    basis_points = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expense_splits", x => new { x.expense_id, x.group_member_id });
                    table.CheckConstraint("ck_expense_splits_share_non_negative", "share_minor >= 0");
                    table.ForeignKey(
                        name: "fk_expense_splits_expenses_expense_id",
                        column: x => x.expense_id,
                        principalTable: "expenses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_expense_splits_group_members_group_member_id",
                        column: x => x.group_member_id,
                        principalTable: "group_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "categories",
                columns: new[] { "id", "created_at", "deleted_at", "group_id", "icon_symbol", "name", "updated_at" },
                values: new object[,]
                {
                    { new Guid("11110000-0000-0000-0000-000000000001"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "cart.fill", "groceries", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("11110000-0000-0000-0000-000000000002"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "fork.knife", "dining", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("11110000-0000-0000-0000-000000000003"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "car.fill", "transport", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("11110000-0000-0000-0000-000000000004"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "house.fill", "housing", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("11110000-0000-0000-0000-000000000005"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "bolt.fill", "utilities", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("11110000-0000-0000-0000-000000000006"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "popcorn.fill", "entertainment", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("11110000-0000-0000-0000-000000000007"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "airplane", "travel", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("11110000-0000-0000-0000-000000000008"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "cross.case.fill", "health", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("11110000-0000-0000-0000-000000000009"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "bag.fill", "shopping", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("11110000-0000-0000-0000-000000000010"), new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "ellipsis.circle.fill", "other", new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });

            migrationBuilder.InsertData(
                table: "change_log_watermark",
                columns: new[] { "one", "pruned_at" },
                values: new object[] { true, null });

            migrationBuilder.InsertData(
                table: "currencies",
                columns: new[] { "code", "minor_units", "symbol" },
                values: new object[,]
                {
                    { "CZK", (short)0, "Kč" },
                    { "EUR", (short)2, "€" },
                    { "GBP", (short)2, "£" },
                    { "USD", (short)2, "$" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_activity_group_time",
                table: "activity_log",
                columns: new[] { "group_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_activity_log_actor_user",
                table: "activity_log",
                column: "actor_user");

            migrationBuilder.CreateIndex(
                name: "ix_auth_identities_user_id",
                table: "auth_identities",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "uq_auth_identities_provider_subject",
                table: "auth_identities",
                columns: new[] { "provider", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_blob_deletions_pending",
                table: "blob_deletions",
                column: "requested_at",
                filter: "confirmed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_categories_global",
                table: "categories",
                column: "name",
                unique: true,
                filter: "group_id IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_categories_group",
                table: "categories",
                columns: new[] { "group_id", "name" },
                unique: true,
                filter: "group_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_change_log_access",
                table: "change_log",
                columns: new[] { "entity_id", "seq" },
                filter: "entity_type = 'access'");

            migrationBuilder.CreateIndex(
                name: "ix_change_log_group",
                table: "change_log",
                columns: new[] { "group_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_author",
                table: "comments",
                column: "author_user");

            migrationBuilder.CreateIndex(
                name: "ix_comments_expense",
                table: "comments",
                columns: new[] { "expense_id", "created_at", "id" },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_comments_group_client",
                table: "comments",
                columns: new[] { "group_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_devices_user_apns_token",
                table: "devices",
                columns: new[] { "user_id", "apns_token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_login_tokens_user_id",
                table: "email_login_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_tokens_expiry",
                table: "email_login_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_email_tokens_lookup",
                table: "email_login_tokens",
                columns: new[] { "email", "purpose", "created_at" },
                descending: new[] { false, false, true },
                filter: "consumed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_splits_member",
                table: "expense_splits",
                column: "group_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_expenses_category_id",
                table: "expenses",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_expenses_created_by",
                table: "expenses",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_expenses_currency",
                table: "expenses",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ix_expenses_group_date",
                table: "expenses",
                columns: new[] { "group_id", "expense_date", "id" },
                descending: new[] { false, true, true },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_expenses_paid_by",
                table: "expenses",
                column: "paid_by");

            migrationBuilder.CreateIndex(
                name: "ix_expenses_recurring",
                table: "expenses",
                column: "recurring_rule_id",
                filter: "recurring_rule_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_expenses_group_client",
                table: "expenses",
                columns: new[] { "group_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_export_results_group_id",
                table: "export_results",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_export_results_user",
                table: "export_results",
                columns: new[] { "requested_by", "requested_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_group_members_former_user_id",
                table: "group_members",
                column: "former_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_group_members_group",
                table: "group_members",
                column: "group_id",
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_group_members_user",
                table: "group_members",
                column: "user_id",
                filter: "user_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_group_members_group_user",
                table: "group_members",
                columns: new[] { "group_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_groups_created_by",
                table: "groups",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_groups_default_currency",
                table: "groups",
                column: "default_currency");

            migrationBuilder.CreateIndex(
                name: "ix_invites_group_id",
                table: "invites",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_invites_invited_by",
                table: "invites",
                column: "invited_by");

            migrationBuilder.CreateIndex(
                name: "ix_invites_member_id",
                table: "invites",
                column: "member_id");

            migrationBuilder.CreateIndex(
                name: "uq_invites_token_hash",
                table: "invites",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_claim",
                table: "jobs",
                columns: new[] { "priority", "run_at" },
                filter: "status = 'queued'");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_reap",
                table: "jobs",
                column: "locked_at",
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "uq_jobs_dedupe",
                table: "jobs",
                column: "dedupe_key",
                unique: true,
                filter: "dedupe_key IS NOT NULL AND status IN ('queued','running')");

            migrationBuilder.CreateIndex(
                name: "ix_notif_prefs_user",
                table: "notification_prefs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_prefs_group_id",
                table: "notification_prefs",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "uq_notif_prefs_global",
                table: "notification_prefs",
                columns: new[] { "user_id", "event_type", "channel" },
                unique: true,
                filter: "group_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_notif_prefs_group",
                table: "notification_prefs",
                columns: new[] { "user_id", "group_id", "event_type", "channel" },
                unique: true,
                filter: "group_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_rate_limit_gc",
                table: "rate_limit_counters",
                column: "window_start");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_rule_splits_group_member_id",
                table: "recurring_rule_splits",
                column: "group_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_due",
                table: "recurring_rules",
                column: "next_run_at",
                filter: "status = 'active' AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_group",
                table: "recurring_rules",
                column: "group_id",
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_rules_category_id",
                table: "recurring_rules",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_rules_created_by",
                table: "recurring_rules",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_rules_currency",
                table: "recurring_rules",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_rules_paid_by",
                table: "recurring_rules",
                column: "paid_by");

            migrationBuilder.CreateIndex(
                name: "uq_recurring_rules_group_client",
                table: "recurring_rules",
                columns: new[] { "group_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_replaced_by",
                table: "refresh_tokens",
                column: "replaced_by");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_user",
                table: "refresh_tokens",
                column: "user_id",
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_settlements_created_by",
                table: "settlements",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_settlements_currency",
                table: "settlements",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ix_settlements_from_member",
                table: "settlements",
                column: "from_member");

            migrationBuilder.CreateIndex(
                name: "ix_settlements_group",
                table: "settlements",
                columns: new[] { "group_id", "settled_on" },
                descending: new[] { false, true },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_settlements_to_member",
                table: "settlements",
                column: "to_member");

            migrationBuilder.CreateIndex(
                name: "uq_settlements_group_client",
                table: "settlements",
                columns: new[] { "group_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_default_currency",
                table: "users",
                column: "default_currency");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                filter: "email IS NOT NULL AND deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_log");

            migrationBuilder.DropTable(
                name: "auth_identities");

            migrationBuilder.DropTable(
                name: "blob_deletions");

            migrationBuilder.DropTable(
                name: "change_log");

            migrationBuilder.DropTable(
                name: "change_log_watermark");

            migrationBuilder.DropTable(
                name: "comments");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "email_login_tokens");

            migrationBuilder.DropTable(
                name: "email_suppressions");

            migrationBuilder.DropTable(
                name: "expense_splits");

            migrationBuilder.DropTable(
                name: "export_results");

            migrationBuilder.DropTable(
                name: "invites");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "notification_cursor");

            migrationBuilder.DropTable(
                name: "notification_prefs");

            migrationBuilder.DropTable(
                name: "rate_limit_counters");

            migrationBuilder.DropTable(
                name: "recurring_rule_splits");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "settlements");

            migrationBuilder.DropTable(
                name: "expenses");

            migrationBuilder.DropTable(
                name: "recurring_rules");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "group_members");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "currencies");
        }
    }
}
