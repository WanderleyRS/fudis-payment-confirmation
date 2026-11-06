using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fudis.Payments.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentEventLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentEventLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProviderEventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Authenticated = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentEventLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEventLog_OrderId",
                table: "PaymentEventLog",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEventLog_Provider_ProviderEventId",
                table: "PaymentEventLog",
                columns: new[] { "Provider", "ProviderEventId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentEventLog");
        }
    }
}
