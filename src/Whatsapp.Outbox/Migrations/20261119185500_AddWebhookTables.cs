using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whatsapp.Outbox.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeliveryReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<int>(type: "integer", nullable: true),
                    ErrorTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboundMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MessageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Sender = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_NotificationId",
                table: "DeliveryReceipts",
                column: "NotificationId");

            migrationBuilder.CreateIndex(
                name: "UX_DeliveryReceipts_Event",
                table: "DeliveryReceipts",
                columns: new[] { "ProviderMessageId", "Status", "OccurredAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_InboundMessages_Message",
                table: "InboundMessages",
                columns: new[] { "TenantId", "MessageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeliveryReceipts");

            migrationBuilder.DropTable(
                name: "InboundMessages");
        }
    }
}
