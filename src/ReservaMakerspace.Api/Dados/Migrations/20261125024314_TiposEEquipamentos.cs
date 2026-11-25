using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReservaMakerspace.Api.Dados.Migrations
{
    /// <inheritdoc />
    public partial class TiposEEquipamentos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:CollationDefinition:nome_sem_diferenciar_caixa", "und-u-ks-level2,und-u-ks-level2,icu,False")
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:btree_gist", ",,");

            migrationBuilder.CreateTable(
                name: "tipos_de_equipamento",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "nome_sem_diferenciar_caixa"),
                    duracao_maxima_pessoal = table.Column<TimeSpan>(type: "interval", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tipos_de_equipamento", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "equipamentos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "nome_sem_diferenciar_caixa"),
                    tipo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ativo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_equipamentos", x => x.id);
                    table.ForeignKey(
                        name: "fk_equipamentos_tipos_de_equipamento_tipo_id",
                        column: x => x.tipo_id,
                        principalTable: "tipos_de_equipamento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_equipamentos_nome",
                table: "equipamentos",
                column: "nome",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_equipamentos_tipo_id",
                table: "equipamentos",
                column: "tipo_id");

            migrationBuilder.CreateIndex(
                name: "ix_tipos_de_equipamento_nome",
                table: "tipos_de_equipamento",
                column: "nome",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equipamentos");

            migrationBuilder.DropTable(
                name: "tipos_de_equipamento");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .OldAnnotation("Npgsql:CollationDefinition:nome_sem_diferenciar_caixa", "und-u-ks-level2,und-u-ks-level2,icu,False")
                .OldAnnotation("Npgsql:PostgresExtension:btree_gist", ",,");
        }
    }
}
