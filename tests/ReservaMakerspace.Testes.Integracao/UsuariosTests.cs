using System.Net;
using System.Net.Http.Json;
using ReservaMakerspace.Contratos.Contas;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

[Collection(ApiCollection.Nome)]
public class UsuariosTests(FabricaDaApi fabrica)
{
    [Fact]
    public async Task Coordenacao_lista_as_pessoas_em_ordem_de_nome()
    {
        var sufixo = Guid.NewGuid().ToString("N");
        var zeca = await fabrica.Contas.CadastrarAsync(AuxiliarDeContas.NovoEmail(), AuxiliarDeContas.NovaSenha(), $"Zeca {sufixo}");
        var abel = await fabrica.Contas.CadastrarAsync(AuxiliarDeContas.NovoEmail(), AuxiliarDeContas.NovaSenha(), $"Abel {sufixo}");
        var coordenacao = await fabrica.Contas.CoordenacaoInicialAsync();
        using var cliente = fabrica.Contas.ClienteCom(coordenacao.Token);

        var resposta = await cliente.GetAsync("/usuarios", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var lista = await Json.LerAsync<List<UsuarioResposta>>(resposta);
        Assert.Contains(abel, lista);
        Assert.Contains(zeca, lista);
        Assert.True(lista.IndexOf(abel) < lista.IndexOf(zeca));
        Assert.Equal(lista.Select(u => u.Nome).Order(StringComparer.Ordinal), lista.Select(u => u.Nome));
    }

    [Fact]
    public async Task Coordenacao_define_o_papel_de_outra_pessoa()
    {
        var alvo = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var coordenacao = await fabrica.Contas.CoordenacaoInicialAsync();
        using var cliente = fabrica.Contas.ClienteCom(coordenacao.Token);

        var resposta = await cliente.PutAsJsonAsync(
            $"/usuarios/{alvo.Id}/papel", new { papel = "Equipe" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var usuario = await Json.LerAsync<UsuarioResposta>(resposta);
        Assert.Equal(alvo.Id, usuario.Id);
        Assert.Equal(Papel.Equipe, usuario.Papel);
    }

    [Fact]
    public async Task Id_inexistente_responde_404()
    {
        var coordenacao = await fabrica.Contas.CoordenacaoInicialAsync();
        using var cliente = fabrica.Contas.ClienteCom(coordenacao.Token);

        var resposta = await cliente.PutAsJsonAsync(
            $"/usuarios/{Guid.NewGuid()}/papel", new { papel = "Equipe" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal("Recurso não encontrado", corpo.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("""{"papel":"Chefe"}""")]
    [InlineData("""{"papel":7}""")]
    [InlineData("""{}""")]
    public async Task Papel_desconhecido_ou_ausente_responde_400(string json)
    {
        var alvo = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var coordenacao = await fabrica.Contas.CoordenacaoInicialAsync();
        using var cliente = fabrica.Contas.ClienteCom(coordenacao.Token);

        var resposta = await cliente.PutAsync(
            $"/usuarios/{alvo.Id}/papel",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        // E o papel da pessoa continua o mesmo.
        using var comoAlvo = fabrica.Contas.ClienteCom(alvo.Token);
        var eu = await Json.LerAsync<UsuarioResposta>(
            await comoAlvo.GetAsync("/me", TestContext.Current.CancellationToken));
        Assert.Equal(Papel.Usuario, eu.Papel);
    }
}
