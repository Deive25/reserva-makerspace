using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReservaMakerspace.Api.Dados.Migrations
{
    /// <inheritdoc />
    public partial class Treinamentos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "treinamentos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    usuario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registrado_por_id = table.Column<Guid>(type: "uuid", nullable: false),
                    realizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valido_ate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revogado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revogado_por_id = table.Column<Guid>(type: "uuid", nullable: true),
                    motivo_da_revogacao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_treinamentos", x => x.id);
                    table.ForeignKey(
                        name: "fk_treinamentos_asp_net_users_registrado_por_id",
                        column: x => x.registrado_por_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_treinamentos_asp_net_users_revogado_por_id",
                        column: x => x.revogado_por_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_treinamentos_asp_net_users_usuario_id",
                        column: x => x.usuario_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_treinamentos_tipos_de_equipamento_tipo_id",
                        column: x => x.tipo_id,
                        principalTable: "tipos_de_equipamento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_treinamentos_registrado_por_id",
                table: "treinamentos",
                column: "registrado_por_id");

            migrationBuilder.CreateIndex(
                name: "ix_treinamentos_revogado_por_id",
                table: "treinamentos",
                column: "revogado_por_id");

            migrationBuilder.CreateIndex(
                name: "ix_treinamentos_tipo_id",
                table: "treinamentos",
                column: "tipo_id");

            migrationBuilder.CreateIndex(
                name: "ix_treinamentos_usuario_id_tipo_id",
                table: "treinamentos",
                columns: new[] { "usuario_id", "tipo_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "treinamentos");
        }
    }
}
