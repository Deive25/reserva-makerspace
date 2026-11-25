using System.Net;
using System.Text.Json;

namespace ReservaMakerspace.Testes.Integracao;

[Collection(ApiCollection.Nome)]
public class DocumentacaoTests(FabricaDaApi fabrica)
{
    [Fact]
    public async Task OpenApi_publicado()
    {
        using var cliente = fabrica.CreateClient();

        var resposta = await cliente.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        using var corpo = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(corpo.RootElement.TryGetProperty("openapi", out _));
    }

    [Fact]
    public async Task OpenApi_declara_o_esquema_bearer_jwt()
    {
        using var cliente = fabrica.CreateClient();

        var resposta = await cliente.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        using var corpo = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var esquema = corpo.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", esquema.GetProperty("type").GetString());
        Assert.Equal("bearer", esquema.GetProperty("scheme").GetString());
        Assert.Equal("JWT", esquema.GetProperty("bearerFormat").GetString());
    }

    [Fact]
    public async Task Scalar_publicado()
    {
        // O cliente segue redirecionamentos por padrão (/scalar pode apontar para /scalar/v1).
        using var cliente = fabrica.CreateClient();

        var resposta = await cliente.GetAsync("/scalar", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("text/html", resposta.Content.Headers.ContentType?.MediaType);
    }
}
