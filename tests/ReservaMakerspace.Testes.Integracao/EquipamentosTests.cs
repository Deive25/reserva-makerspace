using System.Net;
using System.Net.Http.Json;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

[Collection(ApiCollection.Nome)]
public class EquipamentosTests(FabricaDaApi fabrica)
{
    private static string NovoNome(string prefixo = "Maquina") => $"{prefixo} {Guid.NewGuid():N}";

    private async Task<HttpClient> ClienteDaCoordenacaoAsync() =>
        fabrica.Contas.ClienteCom((await fabrica.Contas.CoordenacaoInicialAsync()).Token);

    private static async Task<TipoDeEquipamentoResposta> CriarTipoAsync(HttpClient cliente, string? nome = null)
    {
        var resposta = await cliente.PostAsJsonAsync(
            "/tipos-de-equipamento",
            new { nome = nome ?? NovoNome("Tipo"), duracaoMaximaPessoalMinutos = 120 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await Json.LerAsync<TipoDeEquipamentoResposta>(resposta);
    }

    private static Task<HttpResponseMessage> CriarAsync(HttpClient cliente, string nome, Guid tipoId, bool ativo = true) =>
        cliente.PostAsJsonAsync("/equipamentos", new { nome, tipoId, ativo }, TestContext.Current.CancellationToken);

    private static async Task<EquipamentoResposta> CriarOkAsync(HttpClient cliente, string nome, Guid tipoId, bool ativo = true)
    {
        var resposta = await CriarAsync(cliente, nome, tipoId, ativo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await Json.LerAsync<EquipamentoResposta>(resposta);
    }

    [Fact]
    public async Task Criar_responde_201_com_location_e_o_nome_do_tipo()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var tipo = await CriarTipoAsync(cliente);
        var nome = NovoNome();

        var resposta = await CriarAsync(cliente, nome, tipo.Id);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criada = await Json.LerAsync<EquipamentoResposta>(resposta);
        Assert.Equal(new EquipamentoResposta(criada.Id, nome, tipo.Id, tipo.Nome, true), criada);
        Assert.Equal($"/equipamentos/{criada.Id}", resposta.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Maquina_inativa_aparece_na_lista_e_no_get_por_id_para_qualquer_pessoa()
    {
        using var coordenacao = await ClienteDaCoordenacaoAsync();
        var tipo = await CriarTipoAsync(coordenacao);
        var inativa = await CriarOkAsync(coordenacao, NovoNome(), tipo.Id, ativo: false);
        var usuario = await fabrica.Contas.CriarAsync(Dominio.Papel.Usuario);
        using var cliente = fabrica.Contas.ClienteCom(usuario.Token);

        var lista = await Json.LerAsync<List<EquipamentoResposta>>(
            await cliente.GetAsync("/equipamentos", TestContext.Current.CancellationToken));
        var porId = await Json.LerAsync<EquipamentoResposta>(
            await cliente.GetAsync($"/equipamentos/{inativa.Id}", TestContext.Current.CancellationToken));

        Assert.False(inativa.Ativo);
        Assert.Contains(inativa, lista);
        Assert.Equal(inativa, porId);
        Assert.False(porId.Ativo);
    }

    [Fact]
    public async Task Lista_em_ordem_do_nome_do_tipo_e_depois_do_nome_da_maquina()
    {
        var sufixo = Guid.NewGuid().ToString("N");
        using var cliente = await ClienteDaCoordenacaoAsync();
        var tipoB = await CriarTipoAsync(cliente, $"B tipo {sufixo}");
        var tipoA = await CriarTipoAsync(cliente, $"A tipo {sufixo}");
        var b2 = await CriarOkAsync(cliente, $"Z {sufixo}", tipoB.Id);
        var a2 = await CriarOkAsync(cliente, $"Y {sufixo}", tipoA.Id);
        var a1 = await CriarOkAsync(cliente, $"X {sufixo}", tipoA.Id);

        var lista = await Json.LerAsync<List<EquipamentoResposta>>(
            await cliente.GetAsync("/equipamentos", TestContext.Current.CancellationToken));

        var nossas = lista.Where(e => e.Nome.EndsWith(sufixo, StringComparison.Ordinal)).Select(e => e.Id).ToList();
        Assert.Equal([a1.Id, a2.Id, b2.Id], nossas);
    }

    [Fact]
    public async Task Desativar_e_reativar_pelo_put()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var tipo = await CriarTipoAsync(cliente);
        var maquina = await CriarOkAsync(cliente, NovoNome(), tipo.Id);
        var ct = TestContext.Current.CancellationToken;

        var desativada = await cliente.PutAsJsonAsync(
            $"/equipamentos/{maquina.Id}", new { nome = maquina.Nome, tipoId = tipo.Id, ativo = false }, ct);
        Assert.Equal(HttpStatusCode.OK, desativada.StatusCode);
        Assert.False((await Json.LerAsync<EquipamentoResposta>(desativada)).Ativo);

        var reativada = await cliente.PutAsJsonAsync(
            $"/equipamentos/{maquina.Id}", new { nome = maquina.Nome, tipoId = tipo.Id, ativo = true }, ct);
        Assert.Equal(HttpStatusCode.OK, reativada.StatusCode);
        Assert.True((await Json.LerAsync<EquipamentoResposta>(reativada)).Ativo);
    }

    [Fact]
    public async Task Put_sem_ativo_responde_400_e_nao_reativa()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var tipo = await CriarTipoAsync(cliente);
        var inativa = await CriarOkAsync(cliente, NovoNome(), tipo.Id, ativo: false);
        var ct = TestContext.Current.CancellationToken;

        var resposta = await cliente.PutAsJsonAsync(
            $"/equipamentos/{inativa.Id}", new { nome = NovoNome("Renomeada"), tipoId = tipo.Id }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.True(corpo.RootElement.GetProperty("errors").TryGetProperty("ativo", out _));
        var depois = await Json.LerAsync<EquipamentoResposta>(await cliente.GetAsync($"/equipamentos/{inativa.Id}", ct));
        Assert.Equal(inativa, depois);
    }

    [Fact]
    public async Task Trocar_o_tipo_responde_200()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var tipo = await CriarTipoAsync(cliente);
        var outro = await CriarTipoAsync(cliente);
        var maquina = await CriarOkAsync(cliente, NovoNome(), tipo.Id);

        var resposta = await cliente.PutAsJsonAsync(
            $"/equipamentos/{maquina.Id}",
            new { nome = maquina.Nome, tipoId = outro.Id, ativo = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var atual = await Json.LerAsync<EquipamentoResposta>(resposta);
        Assert.Equal(outro.Id, atual.TipoId);
        Assert.Equal(outro.Nome, atual.TipoNome);
    }

    [Fact]
    public async Task Tipo_inexistente_no_corpo_responde_400_em_errors_tipoId()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var tipo = await CriarTipoAsync(cliente);
        var maquina = await CriarOkAsync(cliente, NovoNome(), tipo.Id);
        var ct = TestContext.Current.CancellationToken;

        var post = await CriarAsync(cliente, NovoNome(), Guid.NewGuid());
        var put = await cliente.PutAsJsonAsync(
            $"/equipamentos/{maquina.Id}", new { nome = maquina.Nome, tipoId = Guid.NewGuid(), ativo = true }, ct);

        foreach (var resposta in new[] { post, put })
        {
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            using var corpo = await Json.DocumentoAsync(resposta);
            Assert.Equal(
                "Tipo de equipamento não encontrado.",
                corpo.RootElement.GetProperty("errors").GetProperty("tipoId")[0].GetString());
        }
    }

    [Fact]
    public async Task Nome_repetido_tambem_com_outra_caixa_responde_409()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var tipo = await CriarTipoAsync(cliente);
        var maquina = await CriarOkAsync(cliente, NovoNome(), tipo.Id);

        var igual = await CriarAsync(cliente, maquina.Nome, tipo.Id);
        var outraCaixa = await CriarAsync(cliente, maquina.Nome.ToUpperInvariant(), tipo.Id);

        await TiposDeEquipamentoTests.ConferirConflitoAsync(igual, "Já existe uma máquina com este nome.");
        await TiposDeEquipamentoTests.ConferirConflitoAsync(outraCaixa, "Já existe uma máquina com este nome.");
    }

    [Fact]
    public async Task Dois_posts_simultaneos_com_o_mesmo_nome_dao_um_201_e_um_409()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var tipo = await CriarTipoAsync(cliente);
        var nome = NovoNome();

        var respostas = await Task.WhenAll(CriarAsync(cliente, nome, tipo.Id), CriarAsync(cliente, nome, tipo.Id));

        Assert.Equal(
            [HttpStatusCode.Created, HttpStatusCode.Conflict],
            respostas.Select(r => r.StatusCode).Order().ToArray());
    }

    [Fact]
    public async Task Put_com_o_proprio_nome_responde_200_e_com_o_nome_de_outra_maquina_responde_409()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var tipo = await CriarTipoAsync(cliente);
        var a = await CriarOkAsync(cliente, NovoNome(), tipo.Id);
        var b = await CriarOkAsync(cliente, NovoNome(), tipo.Id);
        var ct = TestContext.Current.CancellationToken;

        var proprio = await cliente.PutAsJsonAsync(
            $"/equipamentos/{a.Id}", new { nome = a.Nome, tipoId = tipo.Id, ativo = false }, ct);
        var deOutra = await cliente.PutAsJsonAsync(
            $"/equipamentos/{b.Id}", new { nome = a.Nome.ToUpperInvariant(), tipoId = tipo.Id, ativo = true }, ct);

        Assert.Equal(HttpStatusCode.OK, proprio.StatusCode);
        await TiposDeEquipamentoTests.ConferirConflitoAsync(deOutra, "Já existe uma máquina com este nome.");
    }

    [Fact]
    public async Task Nome_com_espacos_nas_pontas_e_gravado_sem_eles_e_so_com_espacos_responde_400()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var tipo = await CriarTipoAsync(cliente);
        var nome = NovoNome();

        var criada = await CriarOkAsync(cliente, $"  {nome}  ", tipo.Id);
        var vazia = await CriarAsync(cliente, "   ", tipo.Id);

        Assert.Equal(nome, criada.Nome);
        Assert.Equal(HttpStatusCode.BadRequest, vazia.StatusCode);
        using var corpo = await Json.DocumentoAsync(vazia);
        Assert.True(corpo.RootElement.GetProperty("errors").TryGetProperty("nome", out _));
    }

    [Fact]
    public async Task Id_inexistente_responde_404_e_id_que_nao_e_guid_tambem()
    {
        using var cliente = await ClienteDaCoordenacaoAsync();
        var tipo = await CriarTipoAsync(cliente);
        var ct = TestContext.Current.CancellationToken;

        var get = await cliente.GetAsync($"/equipamentos/{Guid.NewGuid()}", ct);
        var put = await cliente.PutAsJsonAsync(
            $"/equipamentos/{Guid.NewGuid()}", new { nome = NovoNome(), tipoId = tipo.Id, ativo = true }, ct);
        var naoGuid = await cliente.GetAsync("/equipamentos/abc", ct);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, naoGuid.StatusCode);
    }

    // ---- Reservas futuras (spec §4.6, plano do 1.5, decisão 9) ----

    private static async Task ConferirConflitoComListaAsync(HttpResponseMessage resposta, int quantidade)
    {
        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal("Conflito", corpo.RootElement.GetProperty("title").GetString());
        Assert.Equal(quantidade, corpo.RootElement.GetProperty("conflitos").GetArrayLength());
    }

    private static Task<HttpResponseMessage> AlterarAsync(
        HttpClient cliente, EquipamentoResposta m, string? nome = null, Guid? tipoId = null, bool? ativo = null) =>
        cliente.PutAsJsonAsync(
            $"/equipamentos/{m.Id}",
            new { nome = nome ?? m.Nome, tipoId = tipoId ?? m.TipoId, ativo = ativo ?? m.Ativo },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Desativar_ou_trocar_o_tipo_de_maquina_com_reserva_ativa_e_futura_da_409_com_a_lista_e_renomear_continua_livre()
    {
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        using var coordenacao = await ClienteDaCoordenacaoAsync();
        var tipo = await auxiliar.CriarTipoAsync();
        var outroTipo = await auxiliar.CriarTipoAsync();
        var maquina = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var pessoa = await auxiliar.PessoaTreinadaAsync(tipo.Id);
        using var daPessoa = await auxiliar.ClienteAsync(pessoa);
        var inicio = AuxiliarDeReservas.Daqui(TimeSpan.FromDays(1));
        var reserva = await AuxiliarDeReservas.ReservarOkAsync(daPessoa, maquina.Id, inicio, inicio.AddHours(1));

        await ConferirConflitoComListaAsync(await AlterarAsync(coordenacao, maquina, ativo: false), 1);
        await ConferirConflitoComListaAsync(await AlterarAsync(coordenacao, maquina, tipoId: outroTipo.Id), 1);
        // A máquina continua como estava.
        var depois = await Json.LerAsync<EquipamentoResposta>(
            await coordenacao.GetAsync($"/equipamentos/{maquina.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(maquina, depois);

        // Renomear e salvar sem mudar o tipo nem o estado continuam livres.
        var novoNome = NovoNome("Renomeada");
        var renomeada = await AlterarAsync(coordenacao, maquina, nome: novoNome);
        Assert.Equal(HttpStatusCode.OK, renomeada.StatusCode);
        Assert.Equal(novoNome, (await Json.LerAsync<EquipamentoResposta>(renomeada)).Nome);

        // Com a reserva cancelada, a máquina pode ser desativada, reativada e mudar de tipo.
        Assert.Equal(HttpStatusCode.OK, (await AuxiliarDeReservas.CancelarAsync(daPessoa, reserva.Id)).StatusCode);
        var atual = maquina with { Nome = novoNome };
        var desativada = await AlterarAsync(coordenacao, atual, ativo: false);
        Assert.Equal(HttpStatusCode.OK, desativada.StatusCode);
        Assert.False((await Json.LerAsync<EquipamentoResposta>(desativada)).Ativo);
        Assert.Equal(HttpStatusCode.OK, (await AlterarAsync(coordenacao, atual with { Ativo = false }, ativo: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AlterarAsync(coordenacao, atual, tipoId: outroTipo.Id)).StatusCode);
    }

    [Fact]
    public async Task A_lista_do_409_traz_so_as_reservas_ativas_e_futuras_da_maquina()
    {
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        using var coordenacao = await ClienteDaCoordenacaoAsync();
        var tipo = await auxiliar.CriarTipoAsync();
        var maquina = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var outraMaquina = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var pessoa = await auxiliar.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        using var daPessoa = await auxiliar.ClienteAsync(pessoa);
        var inicio = AuxiliarDeReservas.Daqui(TimeSpan.FromDays(1));
        var cancelada = await AuxiliarDeReservas.ReservarOkAsync(daPessoa, maquina.Id, inicio, inicio.AddHours(1), "DemandaDoParque");
        Assert.Equal(HttpStatusCode.OK, (await AuxiliarDeReservas.CancelarAsync(daPessoa, cancelada.Id)).StatusCode);
        await AuxiliarDeReservas.ReservarOkAsync(daPessoa, maquina.Id, inicio.AddHours(2), inicio.AddHours(3), "DemandaDoParque");
        await AuxiliarDeReservas.ReservarOkAsync(daPessoa, maquina.Id, inicio.AddHours(5), inicio.AddHours(6), "DemandaDoParque");
        await AuxiliarDeReservas.ReservarOkAsync(daPessoa, outraMaquina.Id, inicio, inicio.AddHours(1), "DemandaDoParque");

        var resposta = await AlterarAsync(coordenacao, maquina, ativo: false);

        await ConferirConflitoComListaAsync(resposta, 2);
    }
}
