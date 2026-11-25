using System.Net;
using System.Net.Http.Json;
using ReservaMakerspace.Contratos.Equipamentos;

namespace ReservaMakerspace.Testes.Integracao;

[Collection(ApiCollection.Nome)]
public class TiposDeEquipamentoTests(FabricaDaApi fabrica)
{
    public static TheoryData<int, HttpStatusCode> Duracoes => new()
    {
        { 14, HttpStatusCode.BadRequest },
        { 15, HttpStatusCode.Created },
        { 10_080, HttpStatusCode.Created },
        { 10_081, HttpStatusCode.BadRequest },
    };

    public static TheoryData<string> NomesInvalidos => new()
    {
        "",
        "   ",
        new string('a', 101),
    };

    private static string NovoNome(string prefixo = "Tipo") => $"{prefixo} {Guid.NewGuid():N}";

    private async Task<HttpClient> ClienteDaCoordenacaoAsync() =>
        fabrica.Contas.ClienteCom((await fabrica.Contas.CoordenacaoInicialAsync()).Token);

    private static Task<HttpResponseMessage> CriarAsync(HttpClient cliente, string nome, int minutos = 120) =>
        cliente.PostAsJsonAsync(
            "/tipos-de-equipamento",
            new { nome, duracaoMaximaPessoalMinutos = minutos },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Criar_responde_201_com_location_e_o_get_por_id_devolve_o_mesmo()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var nome = NovoNome();

        var resposta = await CriarAsync(cliente, nome, 120);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criado = await Json.LerAsync<TipoDeEquipamentoResposta>(resposta);
        Assert.Equal(nome, criado.Nome);
        Assert.Equal(120, criado.DuracaoMaximaPessoalMinutos);
        Assert.Equal($"/tipos-de-equipamento/{criado.Id}", resposta.Headers.Location?.OriginalString);

        var busca = await cliente.GetAsync(resposta.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, busca.StatusCode);
        Assert.Equal(criado, await Json.LerAsync<TipoDeEquipamentoResposta>(busca));
    }

    [Fact]
    public async Task Lista_em_ordem_de_nome_para_qualquer_pessoa_logada()
    {
        var sufixo = Guid.NewGuid().ToString("N");
        using var coordenacao = await ClienteDaCoordenacaoAsync();
        var zeta = await Json.LerAsync<TipoDeEquipamentoResposta>(await CriarAsync(coordenacao, $"Zeta {sufixo}"));
        var alfa = await Json.LerAsync<TipoDeEquipamentoResposta>(await CriarAsync(coordenacao, $"Alfa {sufixo}"));
        var usuario = await fabrica.Contas.CriarAsync(Dominio.Papel.Usuario);
        using var cliente = fabrica.Contas.ClienteCom(usuario.Token);

        var resposta = await cliente.GetAsync("/tipos-de-equipamento", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var lista = await Json.LerAsync<List<TipoDeEquipamentoResposta>>(resposta);
        Assert.Contains(alfa, lista);
        Assert.Contains(zeta, lista);
        Assert.True(lista.IndexOf(alfa) < lista.IndexOf(zeta));
    }

    [Fact]
    public async Task Alterar_nome_e_duracao_responde_200()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var criado = await Json.LerAsync<TipoDeEquipamentoResposta>(await CriarAsync(cliente, NovoNome(), 120));
        var novoNome = NovoNome("Renomeado");

        var resposta = await cliente.PutAsJsonAsync(
            $"/tipos-de-equipamento/{criado.Id}",
            new { nome = novoNome, duracaoMaximaPessoalMinutos = 480 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(new TipoDeEquipamentoResposta(criado.Id, novoNome, 480), await Json.LerAsync<TipoDeEquipamentoResposta>(resposta));
        var busca = await Json.LerAsync<TipoDeEquipamentoResposta>(
            await cliente.GetAsync($"/tipos-de-equipamento/{criado.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(novoNome, busca.Nome);
        Assert.Equal(480, busca.DuracaoMaximaPessoalMinutos);
    }

    [Fact]
    public async Task Put_com_o_proprio_nome_responde_200()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var criado = await Json.LerAsync<TipoDeEquipamentoResposta>(await CriarAsync(cliente, NovoNome(), 120));

        var resposta = await cliente.PutAsJsonAsync(
            $"/tipos-de-equipamento/{criado.Id}",
            new { nome = criado.Nome, duracaoMaximaPessoalMinutos = 60 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Put_com_o_nome_de_outro_tipo_responde_409()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var a = await Json.LerAsync<TipoDeEquipamentoResposta>(await CriarAsync(cliente, NovoNome()));
        var b = await Json.LerAsync<TipoDeEquipamentoResposta>(await CriarAsync(cliente, NovoNome()));

        var resposta = await cliente.PutAsJsonAsync(
            $"/tipos-de-equipamento/{b.Id}",
            new { nome = a.Nome.ToUpperInvariant(), duracaoMaximaPessoalMinutos = 60 },
            TestContext.Current.CancellationToken);

        await ConferirConflitoAsync(resposta, "Já existe um tipo com este nome.");
    }

    [Theory]
    [MemberData(nameof(Duracoes))]
    public async Task Duracao_tem_limites_de_15_minutos_a_7_dias(int minutos, HttpStatusCode esperado)
    {
        using var cliente = await ClienteDaCoordenacaoAsync();

        var resposta = await CriarAsync(cliente, NovoNome(), minutos);

        Assert.Equal(esperado, resposta.StatusCode);
        if (esperado == HttpStatusCode.BadRequest)
        {
            using var corpo = await Json.DocumentoAsync(resposta);
            Assert.Equal(
                "A duração máxima precisa ficar entre 15 minutos e 7 dias.",
                corpo.RootElement.GetProperty("errors").GetProperty("duracaoMaximaPessoalMinutos")[0].GetString());
        }
    }

    [Theory]
    [InlineData("""{"nome":"{n}","duracaoMaximaPessoalMinutos":"120"}""")]
    [InlineData("""{"nome":"{n}","duracaoMaximaPessoalMinutos":2.5}""")]
    [InlineData("""{"nome":"{n}"}""")]
    [InlineData("""{"nome":"{n}","duracaoMaximaPessoalMinutos":null}""")]
    public async Task Duracao_em_texto_decimal_ou_ausente_responde_400(string modelo)
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        using var conteudo = new StringContent(
            modelo.Replace("{n}", NovoNome()), System.Text.Encoding.UTF8, "application/json");

        var resposta = await cliente.PostAsync("/tipos-de-equipamento", conteudo, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [MemberData(nameof(NomesInvalidos))]
    public async Task Nome_vazio_so_com_espacos_ou_longo_demais_responde_400_em_errors_nome(string nome)
    {
        using var cliente = await ClienteDaCoordenacaoAsync();

        var resposta = await CriarAsync(cliente, nome);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.True(corpo.RootElement.GetProperty("errors").TryGetProperty("nome", out _));
    }

    [Fact]
    public async Task Nome_com_espacos_nas_pontas_e_gravado_sem_eles()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var nome = NovoNome();

        var criado = await Json.LerAsync<TipoDeEquipamentoResposta>(await CriarAsync(cliente, $"  {nome}  "));

        Assert.Equal(nome, criado.Nome);
    }

    [Fact]
    public async Task Nome_repetido_com_outra_caixa_responde_409_mas_acento_diferente_nao()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var sufixo = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await CriarAsync(cliente, $"Cortadora {sufixo}")).StatusCode);

        var igual = await CriarAsync(cliente, $"CORTADORA {sufixo}");
        var comAcento = await CriarAsync(cliente, $"Cortadorá {sufixo}");

        await ConferirConflitoAsync(igual, "Já existe um tipo com este nome.");
        Assert.Equal(HttpStatusCode.Created, comAcento.StatusCode);
    }

    [Fact]
    public async Task Dois_posts_simultaneos_com_o_mesmo_nome_dao_um_201_e_um_409()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var nome = NovoNome();

        var respostas = await Task.WhenAll(CriarAsync(cliente, nome), CriarAsync(cliente, nome));

        var codigos = respostas.Select(r => r.StatusCode).Order().ToArray();
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], codigos);
    }

    [Fact]
    public async Task Id_inexistente_responde_404_e_id_que_nao_e_guid_tambem()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var ct = TestContext.Current.CancellationToken;

        var get = await cliente.GetAsync($"/tipos-de-equipamento/{Guid.NewGuid()}", ct);
        var put = await cliente.PutAsJsonAsync(
            $"/tipos-de-equipamento/{Guid.NewGuid()}", new { nome = NovoNome(), duracaoMaximaPessoalMinutos = 60 }, ct);
        var naoGuid = await cliente.GetAsync("/tipos-de-equipamento/abc", ct);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, naoGuid.StatusCode);
    }

    internal static async Task ConferirConflitoAsync(HttpResponseMessage resposta, string detalhe)
    {
        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal("Conflito", corpo.RootElement.GetProperty("title").GetString());
        Assert.Equal(detalhe, corpo.RootElement.GetProperty("detail").GetString());
    }
}
