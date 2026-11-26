using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReservaMakerspace.Api.Dados.Migrations
{
    /// <inheritdoc />
    public partial class Manutencoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manutencoes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipamento_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inicio = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fim = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    situacao = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    registrada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    registrada_por_id = table.Column<Guid>(type: "uuid", nullable: false),
                    encerrada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    encerrada_por_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manutencoes", x => x.id);
                    table.CheckConstraint("ck_manutencoes_fim_depois_do_inicio", "fim > inicio");
                    table.ForeignKey(
                        name: "fk_manutencoes_asp_net_users_encerrada_por_id",
                        column: x => x.encerrada_por_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_manutencoes_asp_net_users_registrada_por_id",
                        column: x => x.registrada_por_id,
                        principalTable: "usuarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_manutencoes_equipamentos_equipamento_id",
                        column: x => x.equipamento_id,
                        principalTable: "equipamentos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_manutencoes_encerrada_por_id",
                table: "manutencoes",
                column: "encerrada_por_id");

            migrationBuilder.CreateIndex(
                name: "ix_manutencoes_equipamento_id",
                table: "manutencoes",
                column: "equipamento_id");

            migrationBuilder.CreateIndex(
                name: "ix_manutencoes_registrada_por_id",
                table: "manutencoes",
                column: "registrada_por_id");

            // O EF Core não modela EXCLUDE: a garantia final contra duas manutenções sobrepostas entra por SQL (ADR 0008).
            // Intervalo semiaberto [inicio, fim): a que acaba às 10:00 não conflita com a que começa às 10:00, e é assim que
            // se prorroga. Só as ativas ocupam a agenda: a cancelada sai da restrição. Encerrar só encurta, e por isso nunca
            // a viola. Depende da extensão btree_gist, criada na migração Inicial.
            migrationBuilder.Sql(
                """
                ALTER TABLE manutencoes ADD CONSTRAINT ex_manutencoes_sem_sobreposicao
                    EXCLUDE USING gist (equipamento_id WITH =, tstzrange(inicio, fim, '[)') WITH &&)
                    WHERE (situacao = 'Ativa');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE manutencoes DROP CONSTRAINT ex_manutencoes_sem_sobreposicao;");
            migrationBuilder.DropTable(
                name: "manutencoes");
        }
    }
}
