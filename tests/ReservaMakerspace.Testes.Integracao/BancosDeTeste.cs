using Npgsql;
using Testcontainers.PostgreSql;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// Um container PostgreSQL por classe de teste, com um banco novo e vazio por teste
/// (<c>CREATE DATABASE</c>). Serve aos testes que precisam partir sem nenhuma Coordenação.
/// </summary>
public sealed class BancosDeTeste : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Cria um banco vazio e devolve a connection string dele.</summary>
    public async Task<string> NovoBancoAsync()
    {
        var nome = $"banco_{Guid.NewGuid():N}";
        await using (var fonte = NpgsqlDataSource.Create(_container.GetConnectionString()))
        await using (var comando = fonte.CreateCommand($"CREATE DATABASE {nome}"))
        {
            await comando.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = nome }.ConnectionString;
    }

    public static async Task<long> ContarAsync(string connectionString, string consulta)
    {
        await using var fonte = NpgsqlDataSource.Create(connectionString);
        await using var comando = fonte.CreateCommand(consulta);
        return (long)(await comando.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
