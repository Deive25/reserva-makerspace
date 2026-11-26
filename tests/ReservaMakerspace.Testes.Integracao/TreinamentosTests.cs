using System.Net;
using System.Net.Http.Json;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Contratos.Treinamentos;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// Registro, consulta e revogação de treinamentos, no banco compartilhado e com o relógio do sistema.
/// Cada teste cria as próprias pessoas e o próprio tipo, porque a coleção compartilha o banco.
/// O vencimento e o avanço do tempo ficam em <see cref="ValidadeDoTreinamentoTests"/>.
/// </summary>
[Collection(ApiCollection.Nome)]
public class TreinamentosTests(FabricaDaApi fabrica)
{
    private const string Motivo = "Descarte de resina fora do recipiente.";

    private async Task<TipoDeEquipamentoResposta> CriarTipoAsync()
    {
        using var coordenacao = fabrica.Contas.ClienteCom((await fabrica.Contas.CoordenacaoInicialAsync()).Token);
        var resposta = await coordenacao.PostAsJsonAsync(
            "/tipos-de-equipamento",
            new { nome = $"Tipo {Guid.NewGuid():N}", duracaoMaximaPessoalMinutos = 120 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await Json.LerAsync<TipoDeEquipamentoResposta>(resposta);
    }

    private static Task<HttpResponseMessage> RegistrarAsync(HttpClient cliente, Guid? usuarioId, Guid? tipoId) =>
        cliente.PostAsJsonAsync("/treinamentos", new { usuarioId, tipoId }, TestContext.Current.CancellationToken);

    private static async Task<TreinamentoResposta> RegistrarOkAsync(HttpClient cliente, Guid usuarioId, Guid tipoId)
    {
        var resposta = await RegistrarAsync(cliente, usuarioId, tipoId);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await Json.LerAsync<TreinamentoResposta>(resposta);
    }

    private static Task<HttpResponseMessage> RevogarAsync(HttpClient cliente, Guid id, string? motivo = Motivo) =>
        cliente.PostAsJsonAsync($"/treinamentos/{id}/revogacao", new { motivo }, TestContext.Current.CancellationToken);

    private static async Task<List<TreinamentoResposta>> ListarAsync(HttpClient cliente, string rota) =>
        await Json.LerAsync<List<TreinamentoResposta>>(await cliente.GetAsync(rota, TestContext.Current.CancellationToken));

    private static async Task ConferirErroDeCampoAsync(HttpResponseMessage resposta, string campo)
    {
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.True(corpo.RootElement.GetProperty("errors").TryGetProperty(campo, out _), $"Faltou o erro em errors.{campo}.");
    }

    private static async Task ConferirProblemaAsync(HttpResponseMessage resposta, HttpStatusCode status, string titulo, string detalhe)
    {
        Assert.Equal(status, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal(titulo, corpo.RootElement.GetProperty("title").GetString());
        Assert.Equal(detalhe, corpo.RootElement.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(Papel.Equipe)]
    [InlineData(Papel.Coordenacao)]
    public async Task Equipe_e_coordenacao_registram_um_treinamento_valido_de_12_meses(Papel papel)
    {
        var quemRegistra = await fabrica.Contas.CriarAsync(papel);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipo = await CriarTipoAsync();
        using var cliente = fabrica.Contas.ClienteCom(quemRegistra.Token);

        var resposta = await RegistrarAsync(cliente, pessoa.Id, tipo.Id);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criado = await Json.LerAsync<TreinamentoResposta>(resposta);
        Assert.Equal($"/treinamentos/{criado.Id}", resposta.Headers.Location?.OriginalString);
        Assert.Equal(pessoa.Id, criado.UsuarioId);
        Assert.Equal(tipo.Id, criado.TipoId);
        Assert.Equal(tipo.Nome, criado.TipoNome);
        Assert.Equal(SituacaoDoTreinamento.Valido, criado.Situacao);
        Assert.Equal(criado.RealizadoEm.AddMonths(12), criado.ValidoAte);
        Assert.Equal("Pessoa de Teste", criado.RegistradoPorNome);
        Assert.Null(criado.RevogadoEm);
        Assert.Null(criado.RevogadoPorNome);
        Assert.Null(criado.MotivoDaRevogacao);
    }

    [Fact]
    public async Task A_situacao_sai_como_texto_no_json()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipo = await CriarTipoAsync();
        using var cliente = fabrica.Contas.ClienteCom(equipe.Token);

        var resposta = await RegistrarAsync(cliente, pessoa.Id, tipo.Id);

        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal("Valido", corpo.RootElement.GetProperty("situacao").GetString());
    }

    [Fact]
    public async Task Buscar_por_id_devolve_o_treinamento_e_id_inexistente_da_404()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipo = await CriarTipoAsync();
        using var cliente = fabrica.Contas.ClienteCom(equipe.Token);
        var criado = await RegistrarOkAsync(cliente, pessoa.Id, tipo.Id);

        var encontrado = await Json.LerAsync<TreinamentoResposta>(
            await cliente.GetAsync($"/treinamentos/{criado.Id}", TestContext.Current.CancellationToken));
        var inexistente = await cliente.GetAsync($"/treinamentos/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(criado, encontrado);
        await ConferirProblemaAsync(inexistente, HttpStatusCode.NotFound, "Recurso não encontrado", "Treinamento não encontrado.");
    }

    [Fact]
    public async Task Listar_filtra_por_pessoa_e_sem_filtro_traz_todos()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var ana = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var beto = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipo = await CriarTipoAsync();
        using var cliente = fabrica.Contas.ClienteCom(equipe.Token);
        var deAna = await RegistrarOkAsync(cliente, ana.Id, tipo.Id);
        var deBeto = await RegistrarOkAsync(cliente, beto.Id, tipo.Id);

        var soDaAna = await ListarAsync(cliente, $"/treinamentos?usuarioId={ana.Id}");
        var todos = await ListarAsync(cliente, "/treinamentos");
        var deQuemNaoExiste = await ListarAsync(cliente, $"/treinamentos?usuarioId={Guid.NewGuid()}");

        Assert.Equal([deAna], soDaAna);
        Assert.Contains(deAna, todos);
        Assert.Contains(deBeto, todos);
        Assert.Empty(deQuemNaoExiste);
    }

    [Fact]
    public async Task Me_treinamentos_traz_so_os_da_propria_pessoa_com_token_de_usuario()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var ana = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var beto = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipo = await CriarTipoAsync();
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        var deAna = await RegistrarOkAsync(daEquipe, ana.Id, tipo.Id);
        await RegistrarOkAsync(daEquipe, beto.Id, tipo.Id);
        using var cliente = fabrica.Contas.ClienteCom(ana.Token);

        var resposta = await cliente.GetAsync("/me/treinamentos", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal([deAna], await Json.LerAsync<List<TreinamentoResposta>>(resposta));
    }

    [Fact]
    public async Task Me_treinamentos_ordena_por_tipo_e_do_mais_recente_para_o_mais_antigo()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var ana = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipoA = await CriarTipoAsync();
        var tipoB = await CriarTipoAsync();
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        var antigoDoB = await RegistrarOkAsync(daEquipe, ana.Id, tipoB.Id);
        var doA = await RegistrarOkAsync(daEquipe, ana.Id, tipoA.Id);
        var recenteDoB = await RegistrarOkAsync(daEquipe, ana.Id, tipoB.Id);
        using var cliente = fabrica.Contas.ClienteCom(ana.Token);

        var lista = await ListarAsync(cliente, "/me/treinamentos");

        var primeiroTipo = string.CompareOrdinal(tipoA.Nome, tipoB.Nome) < 0 ? tipoA : tipoB;
        Assert.Equal(primeiroTipo.Id, lista[0].TipoId);
        var doB = lista.Where(t => t.TipoId == tipoB.Id).ToList();
        Assert.Equal([recenteDoB.Id, antigoDoB.Id], doB.Select(t => t.Id));
        Assert.Contains(doA, lista);
    }

    [Theory]
    [InlineData(Papel.Equipe)]
    [InlineData(Papel.Coordenacao)]
    public async Task Ninguem_registra_o_proprio_treinamento_e_responde_422(Papel papel)
    {
        var conta = await fabrica.Contas.CriarAsync(papel);
        var tipo = await CriarTipoAsync();
        using var cliente = fabrica.Contas.ClienteCom(conta.Token);

        var resposta = await RegistrarAsync(cliente, conta.Id, tipo.Id);

        await ConferirProblemaAsync(
            resposta,
            HttpStatusCode.UnprocessableEntity,
            "Regra de negócio violada",
            "Ninguém pode registrar o próprio treinamento.");
        Assert.Empty(await ListarAsync(cliente, $"/treinamentos?usuarioId={conta.Id}"));
    }

    [Fact]
    public async Task Pessoa_ou_tipo_inexistente_no_corpo_da_400_no_campo()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipo = await CriarTipoAsync();
        using var cliente = fabrica.Contas.ClienteCom(equipe.Token);

        await ConferirErroDeCampoAsync(await RegistrarAsync(cliente, Guid.NewGuid(), tipo.Id), "usuarioId");
        await ConferirErroDeCampoAsync(await RegistrarAsync(cliente, pessoa.Id, Guid.NewGuid()), "tipoId");
        await ConferirErroDeCampoAsync(await RegistrarAsync(cliente, null, tipo.Id), "usuarioId");
        await ConferirErroDeCampoAsync(await RegistrarAsync(cliente, pessoa.Id, null), "tipoId");
    }

    [Fact]
    public async Task Revogar_muda_para_revogado_na_hora_e_a_pessoa_ve_o_motivo_e_o_autor()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipo = await CriarTipoAsync();
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        var criado = await RegistrarOkAsync(daEquipe, pessoa.Id, tipo.Id);

        var resposta = await RevogarAsync(daEquipe, criado.Id, $"  {Motivo}  ");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var revogado = await Json.LerAsync<TreinamentoResposta>(resposta);
        Assert.Equal(SituacaoDoTreinamento.Revogado, revogado.Situacao);
        Assert.Equal(Motivo, revogado.MotivoDaRevogacao);
        Assert.Equal("Pessoa de Teste", revogado.RevogadoPorNome);
        Assert.NotNull(revogado.RevogadoEm);
        // Revogado antes dos 12 meses: a validade gravada não muda.
        Assert.Equal(criado.ValidoAte, revogado.ValidoAte);

        using var daPessoa = fabrica.Contas.ClienteCom(pessoa.Token);
        var minha = Assert.Single(await ListarAsync(daPessoa, "/me/treinamentos"));
        Assert.Equal(revogado, minha);
        Assert.Equal(revogado, await Json.LerAsync<TreinamentoResposta>(
            await daEquipe.GetAsync($"/treinamentos/{criado.Id}", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Revogar_o_proprio_treinamento_e_permitido()
    {
        var coordenacao = await fabrica.Contas.CriarAsync(Papel.Coordenacao);
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var tipo = await CriarTipoAsync();
        using var daCoordenacao = fabrica.Contas.ClienteCom(coordenacao.Token);
        var doEquipe = await RegistrarOkAsync(daCoordenacao, equipe.Id, tipo.Id);
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);

        var resposta = await RevogarAsync(daEquipe, doEquipe.Id);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Revogar_de_novo_da_409_e_nao_muda_a_primeira_revogacao()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipo = await CriarTipoAsync();
        using var cliente = fabrica.Contas.ClienteCom(equipe.Token);
        var criado = await RegistrarOkAsync(cliente, pessoa.Id, tipo.Id);
        var primeira = await Json.LerAsync<TreinamentoResposta>(await RevogarAsync(cliente, criado.Id, "Primeiro motivo."));

        var segunda = await RevogarAsync(cliente, criado.Id, "Segundo motivo.");

        await ConferirProblemaAsync(segunda, HttpStatusCode.Conflict, "Conflito", "O treinamento já foi revogado.");
        Assert.Equal(primeira, await Json.LerAsync<TreinamentoResposta>(
            await cliente.GetAsync($"/treinamentos/{criado.Id}", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Motivo_vazio_ou_so_com_espacos_da_400_em_errors_motivo(string? motivo)
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipo = await CriarTipoAsync();
        using var cliente = fabrica.Contas.ClienteCom(equipe.Token);
        var criado = await RegistrarOkAsync(cliente, pessoa.Id, tipo.Id);

        var resposta = await RevogarAsync(cliente, criado.Id, motivo);

        await ConferirErroDeCampoAsync(resposta, "motivo");
        var atual = await Json.LerAsync<TreinamentoResposta>(
            await cliente.GetAsync($"/treinamentos/{criado.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(SituacaoDoTreinamento.Valido, atual.Situacao);
    }

    [Fact]
    public async Task Motivo_com_mais_de_500_caracteres_da_400_e_com_500_passa()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipo = await CriarTipoAsync();
        using var cliente = fabrica.Contas.ClienteCom(equipe.Token);
        var criado = await RegistrarOkAsync(cliente, pessoa.Id, tipo.Id);

        await ConferirErroDeCampoAsync(await RevogarAsync(cliente, criado.Id, new string('a', 501)), "motivo");
        var limite = await RevogarAsync(cliente, criado.Id, new string('a', 500));

        Assert.Equal(HttpStatusCode.OK, limite.StatusCode);
    }

    [Fact]
    public async Task Revogar_id_inexistente_da_404()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        using var cliente = fabrica.Contas.ClienteCom(equipe.Token);

        var resposta = await RevogarAsync(cliente, Guid.NewGuid());

        await ConferirProblemaAsync(resposta, HttpStatusCode.NotFound, "Recurso não encontrado", "Treinamento não encontrado.");
    }

    [Fact]
    public async Task Renovar_e_registrar_outro_os_dois_ficam_no_historico_e_revogar_um_revoga_o_outro()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipo = await CriarTipoAsync();
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        var primeiro = await RegistrarOkAsync(daEquipe, pessoa.Id, tipo.Id);
        var renovado = await RegistrarOkAsync(daEquipe, pessoa.Id, tipo.Id);
        using var daPessoa = fabrica.Contas.ClienteCom(pessoa.Token);

        var daqui = await ListarAsync(daEquipe, $"/treinamentos?usuarioId={pessoa.Id}");
        var minhas = await ListarAsync(daPessoa, "/me/treinamentos");
        Assert.Equal([renovado.Id, primeiro.Id], daqui.Select(t => t.Id));
        Assert.Equal([renovado.Id, primeiro.Id], minhas.Select(t => t.Id));
        Assert.All(minhas, t => Assert.Equal(SituacaoDoTreinamento.Valido, t.Situacao));

        var resposta = await RevogarAsync(daEquipe, renovado.Id);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var depois = await ListarAsync(daPessoa, "/me/treinamentos");
        Assert.Equal(2, depois.Count);
        Assert.All(depois, t =>
        {
            Assert.Equal(SituacaoDoTreinamento.Revogado, t.Situacao);
            Assert.Equal(Motivo, t.MotivoDaRevogacao);
            Assert.Equal("Pessoa de Teste", t.RevogadoPorNome);
        });
    }

    [Fact]
    public async Task Revogar_nao_atinge_outro_tipo_nem_outra_pessoa()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var ana = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var beto = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var tipoA = await CriarTipoAsync();
        var tipoB = await CriarTipoAsync();
        using var cliente = fabrica.Contas.ClienteCom(equipe.Token);
        var alvo = await RegistrarOkAsync(cliente, ana.Id, tipoA.Id);
        var outroTipo = await RegistrarOkAsync(cliente, ana.Id, tipoB.Id);
        var outraPessoa = await RegistrarOkAsync(cliente, beto.Id, tipoA.Id);

        // As reservas futuras (plano do 1.5, decisão 8): a da Ana no tipo A, a da Ana no tipo B e a do Beto no tipo A.
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        var maquinaA = await auxiliar.CriarMaquinaAsync(tipoA.Id);
        var maquinaB = await auxiliar.CriarMaquinaAsync(tipoB.Id);
        using var daAna = fabrica.Contas.ClienteCom(ana.Token);
        using var doBeto = fabrica.Contas.ClienteCom(beto.Token);
        var inicio = AuxiliarDeReservas.Daqui(TimeSpan.FromDays(1));
        var reservaDaAnaNoA = await AuxiliarDeReservas.ReservarOkAsync(daAna, maquinaA.Id, inicio, inicio.AddHours(1));
        var reservaDaAnaNoB = await AuxiliarDeReservas.ReservarOkAsync(daAna, maquinaB.Id, inicio, inicio.AddHours(1));
        var reservaDoBetoNoA = await AuxiliarDeReservas.ReservarOkAsync(doBeto, maquinaA.Id, inicio.AddHours(2), inicio.AddHours(3));

        Assert.Equal(HttpStatusCode.OK, (await RevogarAsync(cliente, alvo.Id)).StatusCode);

        foreach (var intacto in new[] { outroTipo, outraPessoa })
        {
            var atual = await Json.LerAsync<TreinamentoResposta>(
                await cliente.GetAsync($"/treinamentos/{intacto.Id}", TestContext.Current.CancellationToken));
            Assert.Equal(SituacaoDoTreinamento.Valido, atual.Situacao);
        }

        // Só a reserva da pessoa no tipo revogado foi cancelada: com o motivo, quem revogou e a data.
        var daAnaDepois = await AuxiliarDeReservas.ListarAsync(daAna, "/me/reservas");
        var cancelada = Assert.Single(daAnaDepois, r => r.Id == reservaDaAnaNoA.Id);
        Assert.Equal(SituacaoDaReserva.CanceladaPelaEquipe, cancelada.Situacao);
        Assert.Equal("Treinamento revogado", cancelada.MotivoDoCancelamento);
        Assert.Equal("Pessoa de Teste", cancelada.CanceladaPorNome);
        Assert.NotNull(cancelada.CanceladaEm);
        Assert.Equal(SituacaoDaReserva.Ativa, Assert.Single(daAnaDepois, r => r.Id == reservaDaAnaNoB.Id).Situacao);
        var doBetoDepois = await AuxiliarDeReservas.ListarAsync(doBeto, "/me/reservas");
        Assert.Equal(SituacaoDaReserva.Ativa, Assert.Single(doBetoDepois, r => r.Id == reservaDoBetoNoA.Id).Situacao);
    }

    [Fact]
    public async Task Dois_pedidos_de_revogacao_ao_mesmo_tempo_dao_um_200_e_um_409_sem_500()
    {
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        using var cliente = fabrica.Contas.ClienteCom(equipe.Token);

        // Várias rodadas, cada uma com um treinamento novo, para a corrida acontecer de verdade.
        for (var rodada = 0; rodada < 5; rodada++)
        {
            var tipo = await CriarTipoAsync();
            var criado = await RegistrarOkAsync(cliente, pessoa.Id, tipo.Id);
            using var umCliente = fabrica.Contas.ClienteCom(equipe.Token);
            using var outroCliente = fabrica.Contas.ClienteCom(equipe.Token);

            var respostas = await Task.WhenAll(
                RevogarAsync(umCliente, criado.Id, "Pedido um."),
                RevogarAsync(outroCliente, criado.Id, "Pedido dois."));

            var codigos = respostas.Select(r => r.StatusCode).OrderBy(c => (int)c).ToList();
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], codigos);
        }
    }
}
