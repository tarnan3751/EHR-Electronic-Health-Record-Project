using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class DesktopSessionRevokedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "revoked_by",
                schema: "ehr",
                table: "desktop_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_desktop_sessions_revoked_by",
                schema: "ehr",
                table: "desktop_sessions",
                column: "revoked_by");

            migrationBuilder.AddForeignKey(
                name: "fk_desktop_sessions_staff_users_revoked_by",
                schema: "ehr",
                table: "desktop_sessions",
                column: "revoked_by",
                principalSchema: "ehr",
                principalTable: "staff_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_desktop_sessions_staff_users_revoked_by",
                schema: "ehr",
                table: "desktop_sessions");

            migrationBuilder.DropIndex(
                name: "ix_desktop_sessions_revoked_by",
                schema: "ehr",
                table: "desktop_sessions");

            migrationBuilder.DropColumn(
                name: "revoked_by",
                schema: "ehr",
                table: "desktop_sessions");
        }
    }
}
