using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paybitch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SharedLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shared_ledger_groups",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shared_ledger_groups", x => new { x.user_id, x.group_id });
                    table.ForeignKey(
                        name: "fk_shared_ledger_groups_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_shared_ledger_groups_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shared_ledger_links",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shared_ledger_links", x => new { x.user_id, x.member_id });
                    table.ForeignKey(
                        name: "fk_shared_ledger_links_group_members_member_id",
                        column: x => x.member_id,
                        principalTable: "group_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_shared_ledger_links_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_shared_ledger_groups_group_id",
                table: "shared_ledger_groups",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_shared_ledger_links_member_id",
                table: "shared_ledger_links",
                column: "member_id");

            migrationBuilder.CreateIndex(
                name: "ix_shared_ledger_links_person",
                table: "shared_ledger_links",
                columns: new[] { "user_id", "person_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shared_ledger_groups");

            migrationBuilder.DropTable(
                name: "shared_ledger_links");
        }
    }
}
