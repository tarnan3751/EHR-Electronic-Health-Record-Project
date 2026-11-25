using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class UnreadMessagesIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_messages_unread_from_staff",
                schema: "ehr",
                table: "messages",
                column: "patient_id",
                filter: "sender_kind = 'staff' AND read_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_unread_from_staff",
                schema: "ehr",
                table: "messages");
        }
    }
}
