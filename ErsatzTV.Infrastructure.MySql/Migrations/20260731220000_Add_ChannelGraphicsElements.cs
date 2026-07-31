using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Add_ChannelGraphicsElements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelGraphicsElement",
                columns: table => new
                {
                    ChannelId = table.Column<int>(type: "int", nullable: false),
                    GraphicsElementId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelGraphicsElement", x => new { x.ChannelId, x.GraphicsElementId });
                    table.ForeignKey(
                        name: "FK_ChannelGraphicsElement_Channel_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelGraphicsElement_GraphicsElement_GraphicsElementId",
                        column: x => x.GraphicsElementId,
                        principalTable: "GraphicsElement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelGraphicsElement_GraphicsElementId",
                table: "ChannelGraphicsElement",
                column: "GraphicsElementId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelGraphicsElement");
        }
    }
}
