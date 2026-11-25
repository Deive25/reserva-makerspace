using System.Net;
using System.Text.Json;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

[Collection(ApiCollection.Nome)]
public class ErrosTests(FabricaDaApi fabrica)
{
    [Fact]
    public async Task Rota_inexistente_responde_problem_details_em_portugues()
    {
        // Com a FallbackPolicy (toda rota exige login), é preciso mandar token para chegar ao 404.
        var conta = await fabrica.Contas.CriarAsync(Papel.Usuario);
        using var cliente = fabrica.Contas.ClienteCom(conta.Token);

        var resposta = await cliente.GetAsync("/nao-existe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        using var corpo = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Recurso não encontrado", corpo.RootElement.GetProperty("title").GetString());
        Assert.Equal(404, corpo.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Rota_inexistente_sem_token_responde_401()
    {
        // Seguro por padrão: sem login, nem a existência da rota é revelada.
        using var cliente = fabrica.CreateClient();

        var resposta = await cliente.GetAsync("/nao-existe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        using var corpo = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Não autenticado", corpo.RootElement.GetProperty("title").GetString());
    }
}
