using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReservaMakerspace.Api.Dados.Migrations
{
    /// <inheritdoc />
    public partial class Reservas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reservas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipamento_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operador_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inicio = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fim = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finalidade = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    solicitante = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    descricao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    situacao = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelada_por_id = table.Column<Guid>(type: "uuid", nullable: true),
                    motivo_do_cancelamento = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservas", x => x.id);
                    table.CheckConstraint("ck_reservas_fim_depois_do_inicio", "fim > inicio");
                    table.ForeignKey(
                        name: "fk_reservas_asp_net_users_cancelada_por_id",
                        column: x => x.cancelada_por_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservas_asp_net_users_operador_id",
                        column: x => x.operador_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservas_equipamentos_equipamento_id",
                        column: x => x.equipamento_id,
                        principalTable: "equipamentos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reservas_cancelada_por_id",
                table: "reservas",
                column: "cancelada_por_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservas_equipamento_id",
                table: "reservas",
                column: "equipamento_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservas_operador_id",
                table: "reservas",
                column: "operador_id");

            // O EF Core não modela EXCLUDE: a garantia final contra a sobreposição entra por SQL (ADR 0006).
            // Intervalo semiaberto [inicio, fim): quem acaba às 10:00 não conflita com quem começa às 10:00.
            // Só as reservas ativas ocupam a agenda. Depende da extensão btree_gist, criada na migração Inicial.
            migrationBuilder.Sql(
                """
                ALTER TABLE reservas ADD CONSTRAINT ex_reservas_sem_sobreposicao
                    EXCLUDE USING gist (equipamento_id WITH =, tstzrange(inicio, fim, '[)') WITH &&)
                    WHERE (situacao = 'Ativa');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE reservas DROP CONSTRAINT ex_reservas_sem_sobreposicao;");
            migrationBuilder.DropTable(
                name: "reservas");
        }
    }
}
