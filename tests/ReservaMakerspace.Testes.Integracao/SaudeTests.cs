using System.Diagnostics;
using System.Net;
using Npgsql;

namespace ReservaMakerspace.Testes.Integracao;

[Collection(ApiCollection.Nome)]
public class SaudeTests(FabricaDaApi fabrica)
{
    [Fact]
    public async Task Saude_com_banco_no_ar_responde_200()
    {
        using var cliente = fabrica.CreateClient();

        var resposta = await cliente.GetAsync("/saude", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("Healthy", await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Migracao_inicial_cria_btree_gist()
    {
        // Garante que a API subiu (e aplicou as migrações) antes de consultar o banco.
        using var cliente = fabrica.CreateClient();

        await using var fonte = NpgsqlDataSource.Create(fabrica.ConnectionString);
        await using var comando = fonte.CreateCommand("SELECT count(*) FROM pg_extension WHERE extname = 'btree_gist'");
        var total = (long?)await comando.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, total);
    }

    [Fact]
    public async Task Saude_sem_banco_responde_503()
    {
        // Porta 1: ninguém escuta, então a conexão é recusada na hora.
        var conexao = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = "inexistente",
            Username = "ninguem",
            Password = "irrelevante",
            Timeout = 2,
        }.ConnectionString;
        await using var fabricaSemBanco = new FabricaSemBanco(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Makerspace"] = conexao,
            ["Banco:AplicarMigracoesNaSubida"] = "false",
        });
        using var cliente = fabricaSemBanco.CreateClient();

        var relogio = Stopwatch.StartNew();
        var resposta = await cliente.GetAsync("/saude", TestContext.Current.CancellationToken);
        relogio.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        Assert.Equal("Unhealthy", await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(relogio.Elapsed < TimeSpan.FromSeconds(10), $"Demorou {relogio.Elapsed}.");
    }
}
