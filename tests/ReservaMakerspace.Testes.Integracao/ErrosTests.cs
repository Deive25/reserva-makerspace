using System.Net;
using System.Text.Json;

namespace ReservaMakerspace.Testes.Integracao;

[Collection(ApiCollection.Nome)]
public class ErrosTests(FabricaDaApi fabrica)
{
    [Fact]
    public async Task Rota_inexistente_responde_problem_details_em_portugues()
    {
        using var cliente = fabrica.CreateClient();

        var resposta = await cliente.GetAsync("/nao-existe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        using var corpo = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Recurso não encontrado", corpo.RootElement.GetProperty("title").GetString());
        Assert.Equal(404, corpo.RootElement.GetProperty("status").GetInt32());
    }
}
