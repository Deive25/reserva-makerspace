using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ReservaMakerspace.Api.Dados;

/// <summary>Reconhece a violação de uma restrição de exclusão do PostgreSQL (código 23P01), pelo nome da restrição.</summary>
public static class ViolacaoDeExclusao
{
    public static bool Eh(DbUpdateException erro, string restricao) =>
        erro.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } pg
        && pg.ConstraintName == restricao;
}
