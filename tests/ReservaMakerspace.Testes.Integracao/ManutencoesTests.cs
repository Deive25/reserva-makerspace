using System.Net;
using System.Net.Http.Json;
using ReservaMakerspace.Contratos.Manutencoes;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// As rotas de manutenção com o relógio do sistema (plano do 1.7; itens 2, 3, 8 e 9 do Foco, no que não precisa avançar o
/// relógio). Cada teste cria o próprio tipo, as próprias máquinas e as próprias pessoas, porque os testes da coleção
/// compartilham o banco. Os horários têm segundos cheios.
/// </summary>
[Collection(ApiCollection.Nome)]
public class ManutencoesTests(FabricaDaApi fabrica)
{
    private readonly AuxiliarDeReservas _reservas = new(fabrica.Contas);

    private sealed record Cenario(Guid TipoId, Guid MaquinaId, ContaDeTeste Equipe);

    private async Task<Cenario> NovoCenarioAsync()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        return new Cenario(tipo.Id, maquina.Id, await _reservas.EquipeAsync());
    }

    private async Task<HttpClient> ClienteDaEquipeAsync() => await _reservas.ClienteDaEquipeAsync();

    private static async Task<string?> DetalheAsync(HttpResponseMessage resposta)
    {
        using var corpo = await Json.DocumentoAsync(resposta);
        return corpo.RootElement.GetProperty("detail").GetString();
    }

    private async Task<ReservaResposta> VerReservaAsync(ContaDeTeste dono, Guid reservaId)
    {
        using var cliente = await _reservas.ClienteAsync(dono);
        return Assert.Single(await ListarAsync(cliente, "/me/reservas"), r => r.Id == reservaId);
    }

    // ----- Registrar: o formato, a máquina e a regra do início (itens 2) -----

    [Fact]
    public async Task Registrar_devolve_201_com_a_manutencao_e_o_location()
    {
        var c = await NovoCenarioAsync();
        using var equipe = await ClienteDaEquipeAsync();
        var inicio = Dia(20).AddHours(10);

        var resposta = await RegistrarManutencaoAsync(equipe, c.MaquinaId, inicio, inicio.AddHours(3), "Troca do bico.");

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var manutencao = await Json.LerAsync<ManutencaoResposta>(resposta);
        Assert.Equal($"/manutencoes/{manutencao.Id}", resposta.Headers.Location?.OriginalString);
        Assert.Equal(c.MaquinaId, manutencao.EquipamentoId);
        Assert.StartsWith("Máquina ", manutencao.EquipamentoNome);
        Assert.Equal(inicio, manutencao.Inicio);
        Assert.Equal(inicio.AddHours(3), manutencao.Fim);
        Assert.Equal("Troca do bico.", manutencao.Motivo);
        Assert.Equal(SituacaoDaManutencao.Ativa, manutencao.Situacao);
        Assert.Equal("Pessoa de Teste", manutencao.RegistradaPorNome);
        Assert.Null(manutencao.EncerradaEm);
        Assert.Null(manutencao.EncerradaPorNome);

        // O Location aponta para uma rota que existe.
        var lida = await equipe.GetAsync(resposta.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, lida.StatusCode);
        Assert.Equal(manutencao, await Json.LerAsync<ManutencaoResposta>(lida));
    }

    [Fact]
    public async Task A_coordenacao_tambem_registra_e_encerra()
    {
        var c = await NovoCenarioAsync();
        using var coordenacao = await _reservas.ClienteDaCoordenacaoAsync();
        var inicio = Dia(21).AddHours(10);

        var manutencao = await RegistrarManutencaoOkAsync(coordenacao, c.MaquinaId, inicio, inicio.AddHours(2));
        var encerrada = await EncerrarManutencaoAsync(coordenacao, manutencao.Id);

        Assert.Equal(HttpStatusCode.OK, encerrada.StatusCode);
    }

    [Fact]
    public async Task Registrar_numa_maquina_inativa_tambem_e_aceito()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var inativa = await _reservas.CriarMaquinaAsync(tipo.Id, ativo: false);
        using var equipe = await ClienteDaEquipeAsync();
        var inicio = Dia(22).AddHours(10);

        var resposta = await RegistrarManutencaoAsync(equipe, inativa.Id, inicio, inicio.AddHours(2));

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    [Fact]
    public async Task Registrar_numa_maquina_inexistente_responde_404()
    {
        using var equipe = await ClienteDaEquipeAsync();
        var inicio = Dia(23).AddHours(10);

        var resposta = await RegistrarManutencaoAsync(equipe, Guid.NewGuid(), inicio, inicio.AddHours(2));

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("Máquina não encontrada.", await DetalheAsync(resposta));
    }

    [Fact]
    public async Task Registrar_com_formato_invalido_responde_400()
    {
        var c = await NovoCenarioAsync();
        using var equipe = await ClienteDaEquipeAsync();
        var inicio = Dia(24).AddHours(10);
        var rota = $"/equipamentos/{c.MaquinaId}/manutencoes";

        // Cada corpo vai a mão para poder faltar um campo e trazer um horário sem fuso.
        var corpos = new Dictionary<string, object>
        {
            ["sem fim"] = new { inicio, motivo = "Troca do bico." },
            ["fim sem fuso"] = new { inicio, fim = "2030-06-10T12:00:00", motivo = "Troca do bico." },
            ["inicio sem fuso"] = new { inicio = "2030-06-10T10:00:00", fim = inicio.AddHours(2), motivo = "Troca do bico." },
            ["fim igual ao inicio"] = new { inicio, fim = inicio, motivo = "Troca do bico." },
            ["fim antes do inicio"] = new { inicio, fim = inicio.AddHours(-1), motivo = "Troca do bico." },
            ["sem motivo"] = new { inicio, fim = inicio.AddHours(2) },
            ["motivo vazio"] = new { inicio, fim = inicio.AddHours(2), motivo = "" },
            ["motivo so com espacos"] = new { inicio, fim = inicio.AddHours(2), motivo = "   " },
            ["motivo com 501 caracteres"] = new { inicio, fim = inicio.AddHours(2), motivo = new string('a', 501) },
            ["sem inicio e com fim no passado"] = new { fim = Daqui(TimeSpan.FromHours(-1)), motivo = "Troca do bico." },
        };
        foreach (var (nome, corpo) in corpos)
        {
            var resposta = await equipe.PostAsJsonAsync(rota, corpo, TestContext.Current.CancellationToken);
            Assert.True(resposta.StatusCode == HttpStatusCode.BadRequest, $"{nome}: respondeu {resposta.StatusCode}.");
        }

        // E nenhum deles gravou algo.
        var todas = await equipe.GetFromJsonAsync<List<ManutencaoResposta>>(
            $"/equipamentos/{c.MaquinaId}/manutencoes", Json.Opcoes, TestContext.Current.CancellationToken);
        Assert.Empty(todas!);
    }

    [Fact]
    public async Task Motivo_com_500_caracteres_passa()
    {
        var c = await NovoCenarioAsync();
        using var equipe = await ClienteDaEquipeAsync();
        var inicio = Dia(25).AddHours(10);

        var manutencao = await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio, inicio.AddHours(2), new string('a', 500));

        Assert.Equal(500, manutencao.Motivo.Length);
    }

    [Fact]
    public async Task Inicio_no_passado_responde_422()
    {
        var c = await NovoCenarioAsync();
        using var equipe = await ClienteDaEquipeAsync();

        var resposta = await RegistrarManutencaoAsync(equipe, c.MaquinaId, Daqui(TimeSpan.FromMinutes(-5)), Daqui(TimeSpan.FromHours(2)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("O início da manutenção não pode ser no passado.", await DetalheAsync(resposta));
    }

    [Fact]
    public async Task A_ordem_dos_codigos_e_formato_antes_de_maquina_antes_de_regra()
    {
        using var equipe = await ClienteDaEquipeAsync();
        var passado = Daqui(TimeSpan.FromHours(-3));

        // Intervalo invertido e no passado, numa máquina inexistente: o formato (400) vem antes de tudo.
        var invertido = await RegistrarManutencaoAsync(equipe, Guid.NewGuid(), passado, passado.AddHours(-1));
        // Formato certo, máquina inexistente e início no passado: a máquina (404) vem antes da regra (422).
        var inexistente = await RegistrarManutencaoAsync(equipe, Guid.NewGuid(), passado, passado.AddHours(5));

        Assert.Equal(HttpStatusCode.BadRequest, invertido.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
    }

    [Fact]
    public async Task Sem_inicio_a_manutencao_comeca_agora()
    {
        var c = await NovoCenarioAsync();
        using var equipe = await ClienteDaEquipeAsync();

        var antes = DateTimeOffset.UtcNow;
        var manutencao = await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio: null, Daqui(TimeSpan.FromHours(4)));
        var depois = DateTimeOffset.UtcNow;

        Assert.InRange(manutencao.Inicio, antes.AddSeconds(-1), depois.AddSeconds(1));
        Assert.Equal(manutencao.RegistradaEm.ToUnixTimeMilliseconds() / 1000, manutencao.Inicio.ToUnixTimeMilliseconds() / 1000);
    }

    [Fact]
    public async Task O_corpo_sem_a_chave_inicio_tambem_vale_como_agora()
    {
        var c = await NovoCenarioAsync();
        using var equipe = await ClienteDaEquipeAsync();

        var resposta = await equipe.PostAsJsonAsync(
            $"/equipamentos/{c.MaquinaId}/manutencoes",
            new { fim = Daqui(TimeSpan.FromHours(4)), motivo = "Quebrou." },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    // ----- Cancelamento das reservas (item 3) -----

    [Fact]
    public async Task Registrar_cancela_as_reservas_que_cruzam_das_duas_finalidades_e_deixa_as_outras()
    {
        var c = await NovoCenarioAsync();
        var outraMaquina = await _reservas.CriarMaquinaAsync(c.TipoId);
        var dono = await _reservas.PessoaTreinadaAsync(c.TipoId);
        var outroDono = await _reservas.PessoaTreinadaAsync(c.TipoId);
        var treinadoRevogado = await _reservas.PessoaTreinadaAsync(c.TipoId);
        var donoDaOutraMaquina = await _reservas.PessoaTreinadaAsync(c.TipoId);
        var inicio = Dia(5).AddHours(10);
        using var doDono = await _reservas.ClienteAsync(dono);
        using var doOutro = await _reservas.ClienteAsync(outroDono);
        using var daEquipe = await ClienteDaEquipeAsync();
        var equipeTreinada = await _reservas.PessoaTreinadaAsync(c.TipoId, Papel.Equipe);
        using var daEquipeTreinada = fabrica.Contas.ClienteCom(equipeTreinada.Token);

        // A manutenção é de 10:00 às 14:00. Cruzam a contida e a demanda; a que começa antes e a que acaba depois ficam no
        // teste seguinte.
        var contida = await ReservarOkAsync(doDono, c.MaquinaId, inicio.AddHours(1), inicio.AddHours(2));
        var demanda = await ReservarOkAsync(daEquipeTreinada, c.MaquinaId, inicio.AddHours(3), inicio.AddHours(4), "DemandaDoParque", "Prof. Teste");
        // Ficam: encostadas nas duas bordas e a de outra máquina no mesmo horário.
        var terminaNoInicio = await ReservarOkAsync(doDono, c.MaquinaId, inicio.AddHours(-2), inicio);
        var comecaNoFim = await ReservarOkAsync(doOutro, c.MaquinaId, inicio.AddHours(4), inicio.AddHours(5));
        using var doDaOutra = await _reservas.ClienteAsync(donoDaOutraMaquina);
        var deOutraMaquina = await ReservarOkAsync(doDaOutra, outraMaquina.Id, inicio, inicio.AddHours(1));
        // Já cancelada antes (a revogação do treinamento cancela com o motivo dela): o motivo de antes fica.
        using var doRevogado = await _reservas.ClienteAsync(treinadoRevogado);
        var jaCancelada = await ReservarOkAsync(doRevogado, c.MaquinaId, inicio.AddHours(2), inicio.AddHours(3));
        var treinamentos = await ListarTreinamentosAsync(daEquipe, treinadoRevogado.Id);
        foreach (var treinamento in treinamentos)
        {
            Assert.Equal(HttpStatusCode.OK, (await RevogarAsync(treinamento)).StatusCode);
        }

        var antes = DateTimeOffset.UtcNow;
        var manutencao = await RegistrarManutencaoOkAsync(daEquipe, c.MaquinaId, inicio, inicio.AddHours(4), "Troca do bico.");
        var depois = DateTimeOffset.UtcNow;

        // A resposta do 201 não lista as reservas canceladas: a Equipe as vê em GET /reservas?equipamentoId=.
        var todas = await ListarAsync(daEquipe, $"/reservas?equipamentoId={c.MaquinaId}");
        foreach (var cancelada in new[] { contida, demanda })
        {
            var r = Assert.Single(todas, x => x.Id == cancelada.Id);
            Assert.Equal(SituacaoDaReserva.CanceladaPorManutencao, r.Situacao);
            Assert.Equal("Troca do bico.", r.MotivoDoCancelamento);
            Assert.Equal("Pessoa de Teste", r.CanceladaPorNome);
            Assert.InRange(r.CanceladaEm!.Value, antes.AddSeconds(-1), depois.AddSeconds(1));
        }

        foreach (var intacta in new[] { terminaNoInicio, comecaNoFim })
        {
            Assert.Equal(SituacaoDaReserva.Ativa, Assert.Single(todas, x => x.Id == intacta.Id).Situacao);
        }

        var antiga = Assert.Single(todas, x => x.Id == jaCancelada.Id);
        Assert.Equal(SituacaoDaReserva.CanceladaPelaEquipe, antiga.Situacao);
        Assert.Equal("Treinamento revogado", antiga.MotivoDoCancelamento);

        var daOutra = await ListarAsync(daEquipe, $"/reservas?equipamentoId={outraMaquina.Id}");
        Assert.Equal(SituacaoDaReserva.Ativa, Assert.Single(daOutra, x => x.Id == deOutraMaquina.Id).Situacao);

        // O dono vê o cancelamento em /me/reservas, com o motivo, quem registrou e a data.
        var vista = await VerReservaAsync(dono, contida.Id);
        Assert.Equal(SituacaoDaReserva.CanceladaPorManutencao, vista.Situacao);
        Assert.Equal("Troca do bico.", vista.MotivoDoCancelamento);
        Assert.Equal("Pessoa de Teste", vista.CanceladaPorNome);
        Assert.NotNull(vista.CanceladaEm);
        Assert.Equal(manutencao.Motivo, vista.MotivoDoCancelamento);
    }

    [Fact]
    public async Task Registrar_cancela_a_reserva_que_comeca_antes_e_a_que_acaba_depois_mas_nao_as_que_so_encostam()
    {
        var c = await NovoCenarioAsync();
        var dono = await _reservas.PessoaTreinadaAsync(c.TipoId);
        var outroDono = await _reservas.PessoaTreinadaAsync(c.TipoId);
        using var doDono = await _reservas.ClienteAsync(dono);
        using var doOutro = await _reservas.ClienteAsync(outroDono);
        using var daEquipe = await ClienteDaEquipeAsync();
        var inicio = Dia(4).AddHours(10);

        // A manutenção é de 10:00 às 12:00.
        var comecaAntes = await ReservarOkAsync(doDono, c.MaquinaId, inicio.AddHours(-1), inicio.AddHours(1));
        var acabaDepois = await ReservarOkAsync(doOutro, c.MaquinaId, inicio.AddHours(1), inicio.AddHours(3));
        var antes = await ReservarOkAsync(doDono, c.MaquinaId, inicio.AddHours(-2), inicio.AddHours(-1));
        var depois = await ReservarOkAsync(doOutro, c.MaquinaId, inicio.AddHours(3), inicio.AddHours(4));

        await RegistrarManutencaoOkAsync(daEquipe, c.MaquinaId, inicio, inicio.AddHours(2), "Alinhar o eixo.");

        Assert.Equal(SituacaoDaReserva.CanceladaPorManutencao, (await VerReservaAsync(dono, comecaAntes.Id)).Situacao);
        Assert.Equal(SituacaoDaReserva.CanceladaPorManutencao, (await VerReservaAsync(outroDono, acabaDepois.Id)).Situacao);
        Assert.Equal(SituacaoDaReserva.Ativa, (await VerReservaAsync(dono, antes.Id)).Situacao);
        Assert.Equal(SituacaoDaReserva.Ativa, (await VerReservaAsync(outroDono, depois.Id)).Situacao);
    }

    [Fact]
    public async Task Registrar_sem_reservas_no_intervalo_nao_cancela_nada_e_so_grava_a_manutencao()
    {
        var c = await NovoCenarioAsync();
        var dono = await _reservas.PessoaTreinadaAsync(c.TipoId);
        using var doDono = await _reservas.ClienteAsync(dono);
        using var daEquipe = await ClienteDaEquipeAsync();
        var inicio = Dia(6).AddHours(10);
        var reserva = await ReservarOkAsync(doDono, c.MaquinaId, inicio, inicio.AddHours(1));

        await RegistrarManutencaoOkAsync(daEquipe, c.MaquinaId, inicio.AddDays(1), inicio.AddDays(1).AddHours(2));

        Assert.Equal(SituacaoDaReserva.Ativa, (await VerReservaAsync(dono, reserva.Id)).Situacao);
    }

    // ----- Duas manutenções (item 8) -----

    [Fact]
    public async Task Manutencoes_sobrepostas_na_mesma_maquina_dao_409_com_a_lista_manutencoes()
    {
        var c = await NovoCenarioAsync();
        using var equipe = await ClienteDaEquipeAsync();
        var inicio = Dia(30).AddHours(10);
        var primeira = await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio, inicio.AddHours(4), "Primeira.");

        var resposta = await RegistrarManutencaoAsync(equipe, c.MaquinaId, inicio.AddHours(2), inicio.AddHours(6), "Segunda.");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal("A máquina já tem uma manutenção ativa nesse horário.", corpo.RootElement.GetProperty("detail").GetString());
        var item = Assert.Single(corpo.RootElement.GetProperty("manutencoes").EnumerateArray());
        Assert.Equal(primeira.Id, item.GetProperty("manutencaoId").GetGuid());
        Assert.Equal(inicio, item.GetProperty("inicio").GetDateTimeOffset());
        Assert.Equal(inicio.AddHours(4), item.GetProperty("fim").GetDateTimeOffset());
        Assert.Equal("Primeira.", item.GetProperty("motivo").GetString());
        // Quem registrou fica só nas rotas da Equipe.
        Assert.Equal(4, item.EnumerateObject().Count());

        // E nada foi gravado.
        var todas = await equipe.GetFromJsonAsync<List<ManutencaoResposta>>(
            $"/equipamentos/{c.MaquinaId}/manutencoes", Json.Opcoes, TestContext.Current.CancellationToken);
        Assert.Single(todas!);
    }

    [Fact]
    public async Task O_409_de_manutencoes_nao_cancela_as_reservas_do_pedido_recusado()
    {
        var c = await NovoCenarioAsync();
        var dono = await _reservas.PessoaTreinadaAsync(c.TipoId);
        using var doDono = await _reservas.ClienteAsync(dono);
        using var equipe = await ClienteDaEquipeAsync();
        var inicio = Dia(6).AddHours(10);
        await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio, inicio.AddHours(2));
        // Uma reserva depois da manutenção: o pedido recusado (que a cruzaria) não pode cancelá-la.
        var reserva = await ReservarOkAsync(doDono, c.MaquinaId, inicio.AddHours(3), inicio.AddHours(4));

        var resposta = await RegistrarManutencaoAsync(equipe, c.MaquinaId, inicio.AddHours(1), inicio.AddHours(5));

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal(SituacaoDaReserva.Ativa, (await VerReservaAsync(dono, reserva.Id)).Situacao);
    }

    [Fact]
    public async Task Manutencoes_encostadas_ou_em_outra_maquina_entram()
    {
        var c = await NovoCenarioAsync();
        var outra = await _reservas.CriarMaquinaAsync(c.TipoId);
        using var equipe = await ClienteDaEquipeAsync();
        var inicio = Dia(32).AddHours(10);
        await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio, inicio.AddHours(2));

        // Prorrogar é registrar outra encostada no fim, e uma encostada no início também entra.
        await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio.AddHours(2), inicio.AddHours(4));
        await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio.AddHours(-2), inicio);
        await RegistrarManutencaoOkAsync(equipe, outra.Id, inicio, inicio.AddHours(2));
    }

    [Fact]
    public async Task Manutencao_por_cima_de_uma_cancelada_entra()
    {
        var c = await NovoCenarioAsync();
        using var equipe = await ClienteDaEquipeAsync();
        var inicio = Dia(33).AddHours(10);
        var primeira = await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio, inicio.AddHours(4));
        Assert.Equal(HttpStatusCode.OK, (await EncerrarManutencaoAsync(equipe, primeira.Id)).StatusCode);

        await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio.AddHours(1), inicio.AddHours(3));
    }

    // ----- Listar e buscar -----

    [Fact]
    public async Task Listar_traz_todas_as_da_maquina_inclusive_as_canceladas_da_mais_recente_para_a_mais_antiga()
    {
        var c = await NovoCenarioAsync();
        var outra = await _reservas.CriarMaquinaAsync(c.TipoId);
        using var equipe = await ClienteDaEquipeAsync();
        var inicio = Dia(34).AddHours(10);
        var primeira = await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio, inicio.AddHours(2));
        var terceira = await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio.AddDays(2), inicio.AddDays(2).AddHours(2));
        var segunda = await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio.AddDays(1), inicio.AddDays(1).AddHours(2));
        await EncerrarManutencaoAsync(equipe, segunda.Id);
        await RegistrarManutencaoOkAsync(equipe, outra.Id, inicio, inicio.AddHours(2));

        var lista = await equipe.GetFromJsonAsync<List<ManutencaoResposta>>(
            $"/equipamentos/{c.MaquinaId}/manutencoes", Json.Opcoes, TestContext.Current.CancellationToken);

        Assert.Equal([terceira.Id, segunda.Id, primeira.Id], lista!.Select(m => m.Id).ToList());
        Assert.Equal(SituacaoDaManutencao.Cancelada, lista![1].Situacao);
    }

    [Fact]
    public async Task Listar_de_uma_maquina_inexistente_responde_404_e_sem_manutencoes_responde_lista_vazia()
    {
        var c = await NovoCenarioAsync();
        using var equipe = await ClienteDaEquipeAsync();

        var inexistente = await equipe.GetAsync($"/equipamentos/{Guid.NewGuid()}/manutencoes", TestContext.Current.CancellationToken);
        var vazia = await equipe.GetAsync($"/equipamentos/{c.MaquinaId}/manutencoes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Equal(HttpStatusCode.OK, vazia.StatusCode);
        Assert.Empty(await Json.LerAsync<List<ManutencaoResposta>>(vazia));
    }

    [Fact]
    public async Task Buscar_uma_manutencao_inexistente_responde_404()
    {
        using var equipe = await ClienteDaEquipeAsync();

        var resposta = await equipe.GetAsync($"/manutencoes/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("Manutenção não encontrada.", await DetalheAsync(resposta));
    }

    // ----- Encerrar (item 9, no que não precisa avançar o relógio) -----

    [Fact]
    public async Task Encerrar_uma_que_ainda_nao_comecou_cancela_e_guarda_quem_e_quando()
    {
        var c = await NovoCenarioAsync();
        var dono = await _reservas.PessoaTreinadaAsync(c.TipoId);
        using var doDono = await _reservas.ClienteAsync(dono);
        using var registrou = await ClienteDaEquipeAsync();
        var outraEquipe = await _reservas.PessoaTreinadaAsync(c.TipoId, Papel.Equipe);
        using var quemEncerra = fabrica.Contas.ClienteCom(outraEquipe.Token);
        var inicio = Dia(7).AddHours(10);
        var reserva = await ReservarOkAsync(doDono, c.MaquinaId, inicio, inicio.AddHours(1));
        var manutencao = await RegistrarManutencaoOkAsync(registrou, c.MaquinaId, inicio, inicio.AddHours(3));

        var antes = DateTimeOffset.UtcNow;
        var resposta = await EncerrarManutencaoAsync(quemEncerra, manutencao.Id);

        // Qualquer pessoa da Equipe encerra, e não só quem registrou.
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var encerrada = await Json.LerAsync<ManutencaoResposta>(resposta);
        Assert.Equal(SituacaoDaManutencao.Cancelada, encerrada.Situacao);
        Assert.Equal(inicio, encerrada.Inicio);
        Assert.Equal(inicio.AddHours(3), encerrada.Fim);
        Assert.Equal("Pessoa de Teste", encerrada.EncerradaPorNome);
        Assert.InRange(encerrada.EncerradaEm!.Value, antes.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        // As reservas que a manutenção cancelou continuam canceladas (§4.4).
        Assert.Equal(SituacaoDaReserva.CanceladaPorManutencao, (await VerReservaAsync(dono, reserva.Id)).Situacao);
    }

    [Fact]
    public async Task Encerrar_de_novo_uma_cancelada_responde_409_e_um_id_inexistente_404()
    {
        var c = await NovoCenarioAsync();
        using var equipe = await ClienteDaEquipeAsync();
        var inicio = Dia(8).AddHours(10);
        var manutencao = await RegistrarManutencaoOkAsync(equipe, c.MaquinaId, inicio, inicio.AddHours(3));
        Assert.Equal(HttpStatusCode.OK, (await EncerrarManutencaoAsync(equipe, manutencao.Id)).StatusCode);

        var segunda = await EncerrarManutencaoAsync(equipe, manutencao.Id);
        var inexistente = await EncerrarManutencaoAsync(equipe, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        Assert.Equal("A manutenção já foi cancelada.", await DetalheAsync(segunda));
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Equal("Manutenção não encontrada.", await DetalheAsync(inexistente));
    }

    private async Task<List<Guid>> ListarTreinamentosAsync(HttpClient equipe, Guid usuarioId)
    {
        var resposta = await equipe.GetAsync($"/treinamentos?usuarioId={usuarioId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        return corpo.RootElement.EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToList();
    }

    private Task<HttpResponseMessage> RevogarAsync(Guid treinamentoId) => _reservas.RevogarAsync(treinamentoId);
}
