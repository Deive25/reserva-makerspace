using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// Sobe a API de verdade contra um PostgreSQL 17 real (Testcontainers).
/// Uma instância é compartilhada por todos os testes da coleção <see cref="ApiCollection"/>.
/// </summary>
public sealed class FabricaDaApi : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _banco = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _banco.GetConnectionString();

    public async ValueTask InitializeAsync() => await _banco.StartAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Makerspace", ConnectionString);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _banco.DisposeAsync();
    }
}

/// <summary>
/// Fábrica sem container: serve aos testes de subida e de banco fora do ar,
/// que passam a configuração que quiserem (ou nenhuma).
/// </summary>
public sealed class FabricaSemBanco(IReadOnlyDictionary<string, string?> configuracao) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (chave, valor) in configuracao)
        {
            if (valor is not null)
            {
                builder.UseSetting(chave, valor);
            }
        }
    }
}
