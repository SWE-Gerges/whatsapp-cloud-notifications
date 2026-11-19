using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whatsapp.Outbox.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptJoinColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FallbackNotificationId",
                table: "NotificationLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAttemptAt",
                table: "NotificationLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_ProviderMessageId",
                table: "NotificationLogs",
                column: "ProviderMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationLogs_ProviderMessageId",
                table: "NotificationLogs");

            migrationBuilder.DropColumn(
                name: "FallbackNotificationId",
                table: "NotificationLogs");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "NotificationLogs");
        }
    }
}
