using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ReservaMakerspace.Api.Dados;

/// <summary>Reconhece a violação de um índice único do PostgreSQL (código 23505), pelo nome do índice.</summary>
public static class ViolacaoDeUnicidade
{
    public static bool Eh(DbUpdateException erro, string indice) =>
        erro.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == indice;
}
