using System.Net;
using System.Text.Json;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// Onde a manutenção aparece e o que ela bloqueia (plano do 1.7, decisões 7 a 10; itens 4 a 7 e a parte do 11 que fala de
/// <c>manutencoes</c> nos 409), no banco compartilhado e com o relógio do sistema. Cada teste cria o próprio tipo, as
/// próprias máquinas e as próprias pessoas. As bordas que dependem de avançar o relógio ficam em
/// <c>ManutencoesComRelogioTests</c>.
/// </summary>
[Collection(ApiCollection.Nome)]
public class ManutencaoNaAgendaTests(FabricaDaApi fabrica)
{
    private const string MensagemDaManutencao = "A máquina está em manutenção nesse horário.";

    private const string MensagemDoConflito = "A máquina já tem uma reserva ativa nesse horário.";

    private const string Motivo = "Visita técnica do parque.";

    private readonly AuxiliarDeReservas _reservas = new(fabrica.Contas);

    private static string RotaDaAgenda(Guid maquinaId, DateTimeOffset de, DateTimeOffset ate) =>
        $"/equipamentos/{maquinaId}/agenda?de={Horario(de)}&ate={Horario(ate)}";

    private static string RotaDaDisponibilidade(Guid tipoId, DateTimeOffset inicio, DateTimeOffset fim) =>
        $"/disponibilidade?tipoId={tipoId}&inicio={Horario(inicio)}&fim={Horario(fim)}";

    private static async Task<JsonDocument> LerAsync(HttpClient cliente, string rota)
    {
        var resposta = await cliente.GetAsync(rota, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return await Json.DocumentoAsync(resposta);
    }

    private static async Task<JsonDocument> LerConflitoAsync(HttpResponseMessage resposta)
    {
        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        return await Json.DocumentoAsync(resposta);
    }

    private static string Detalhe(JsonDocument corpo) => corpo.RootElement.GetProperty("detail").GetString()!;

    private static List<Guid> Ids(JsonElement lista, string propriedade) =>
        lista.EnumerateArray().Select(i => i.GetProperty(propriedade).GetGuid()).ToList();

    private async Task<ReservaResposta> VerReservaAsync(ContaDeTeste dono, Guid reservaId)
    {
        using var cliente = await _reservas.ClienteAsync(dono);
        return Assert.Single(await ListarAsync(cliente, "/me/reservas"), r => r.Id == reservaId);
    }

    // ----- A agenda (item 7) -----

    [Fact]
    public async Task A_agenda_traz_as_manutencoes_ativas_que_cruzam_a_janela_inclusive_nas_pontas_em_ordem_de_inicio()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        using var equipe = await _reservas.ClienteDaEquipeAsync();
        var dia = Dia(3);

        // A janela é [10h, 14h). Nascem fora da ordem de início.
        var acabaDepois = await RegistrarManutencaoOkAsync(equipe, maquina.Id, dia.AddHours(13), dia.AddHours(16), "Termina depois.");
        var dentro = await RegistrarManutencaoOkAsync(equipe, maquina.Id, dia.AddHours(11), dia.AddHours(12), "Dentro.");
        var comecaAntes = await RegistrarManutencaoOkAsync(equipe, maquina.Id, dia.AddHours(8), dia.AddHours(10).AddMinutes(30), "Começa antes.");

        using var agenda = await LerAsync(equipe, RotaDaAgenda(maquina.Id, dia.AddHours(10), dia.AddHours(14)));

        var manutencoes = agenda.RootElement.GetProperty("manutencoes");
        Assert.Equal([comecaAntes.Id, dentro.Id, acabaDepois.Id], Ids(manutencoes, "manutencaoId"));
        Assert.Equal(
            ["Começa antes.", "Dentro.", "Termina depois."],
            manutencoes.EnumerateArray().Select(m => m.GetProperty("motivo").GetString()).ToList());
        // Os horários são os da manutenção inteira, e não os recortados pela janela.
        Assert.Equal(dia.AddHours(8), manutencoes[0].GetProperty("inicio").GetDateTimeOffset());
        Assert.Equal(dia.AddHours(16), manutencoes[2].GetProperty("fim").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_agenda_deixa_de_fora_as_encostadas_nas_bordas_as_canceladas_e_as_de_outra_maquina()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var outra = await _reservas.CriarMaquinaAsync(tipo.Id);
        using var equipe = await _reservas.ClienteDaEquipeAsync();
        var dia = Dia(3);

        // A janela é [10h, 14h): uma acaba às 10h, outra começa às 14h, uma foi cancelada e outra é de outra máquina.
        await RegistrarManutencaoOkAsync(equipe, maquina.Id, dia.AddHours(8), dia.AddHours(10));
        await RegistrarManutencaoOkAsync(equipe, maquina.Id, dia.AddHours(14), dia.AddHours(16));
        var cancelada = await RegistrarManutencaoOkAsync(equipe, maquina.Id, dia.AddHours(11), dia.AddHours(12));
        Assert.Equal(HttpStatusCode.OK, (await EncerrarManutencaoAsync(equipe, cancelada.Id)).StatusCode);
        await RegistrarManutencaoOkAsync(equipe, outra.Id, dia.AddHours(11), dia.AddHours(12));

        using var agenda = await LerAsync(equipe, RotaDaAgenda(maquina.Id, dia.AddHours(10), dia.AddHours(14)));

        Assert.Equal(0, agenda.RootElement.GetProperty("manutencoes").GetArrayLength());
    }

    [Fact]
    public async Task O_usuario_e_a_equipe_veem_os_mesmos_campos_da_manutencao_sem_o_nome_de_quem_registrou()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        using var equipe = await _reservas.ClienteDaEquipeAsync();
        using var usuario = fabrica.Contas.ClienteCom((await fabrica.Contas.CriarAsync(Papel.Usuario)).Token);
        var dia = Dia(3);
        var manutencao = await RegistrarManutencaoOkAsync(equipe, maquina.Id, dia.AddHours(11), dia.AddHours(12), "Troca do bico.");
        var rota = RotaDaAgenda(maquina.Id, dia.AddHours(10), dia.AddHours(14));

        using var daEquipe = await LerAsync(equipe, rota);
        using var doUsuario = await LerAsync(usuario, rota);

        foreach (var agenda in new[] { daEquipe, doUsuario })
        {
            var item = Assert.Single(agenda.RootElement.GetProperty("manutencoes").EnumerateArray());
            Assert.Equal(
                ["fim", "inicio", "manutencaoId", "motivo"],
                item.EnumerateObject().Select(p => p.Name).Order().ToList());
            Assert.Equal(manutencao.Id, item.GetProperty("manutencaoId").GetGuid());
            Assert.Equal("Troca do bico.", item.GetProperty("motivo").GetString());
        }

        Assert.Equal(
            daEquipe.RootElement.GetProperty("manutencoes").GetRawText(),
            doUsuario.RootElement.GetProperty("manutencoes").GetRawText());
    }

    [Fact]
    public async Task A_agenda_sem_manutencoes_traz_a_lista_vazia()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        using var equipe = await _reservas.ClienteDaEquipeAsync();
        var dia = Dia(3);

        using var agenda = await LerAsync(equipe, RotaDaAgenda(maquina.Id, dia.AddHours(10), dia.AddHours(14)));

        Assert.Equal(JsonValueKind.Array, agenda.RootElement.GetProperty("manutencoes").ValueKind);
        Assert.Equal(0, agenda.RootElement.GetProperty("manutencoes").GetArrayLength());
    }

    // ----- O bloqueio de reservas novas (item 4) -----

    [Fact]
    public async Task Reserva_pessoal_que_cruza_uma_manutencao_da_409_com_a_lista_manutencoes()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
        using var equipe = await _reservas.ClienteDaEquipeAsync();
        using var doDono = fabrica.Contas.ClienteCom(dono.Token);
        var dia = Dia(3);
        var manutencao = await RegistrarManutencaoOkAsync(equipe, maquina.Id, dia.AddHours(10), dia.AddHours(12), "Troca do bico.");

        // Cruzando o começo, contida, cruzando o fim e idêntica.
        foreach (var (inicio, fim) in new[]
        {
            (dia.AddHours(9), dia.AddHours(11)), (dia.AddHours(10).AddMinutes(30), dia.AddHours(11)),
            (dia.AddHours(11), dia.AddHours(13)), (dia.AddHours(10), dia.AddHours(12)),
        })
        {
            using var corpo = await LerConflitoAsync(await ReservarAsync(doDono, maquina.Id, inicio, fim));

            Assert.Equal(MensagemDaManutencao, Detalhe(corpo));
            var item = Assert.Single(corpo.RootElement.GetProperty("manutencoes").EnumerateArray());
            Assert.Equal(manutencao.Id, item.GetProperty("manutencaoId").GetGuid());
            Assert.Equal("Troca do bico.", item.GetProperty("motivo").GetString());
            // As outras duas listas continuam presentes: sem reservas em conflito e sem outra máquina do tipo.
            Assert.Equal(0, corpo.RootElement.GetProperty("conflitos").GetArrayLength());
            Assert.Equal(0, corpo.RootElement.GetProperty("maquinasLivres").GetArrayLength());
        }
    }

    [Fact]
    public async Task Demanda_que_cruza_uma_manutencao_da_409_com_ou_sem_motivo()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var equipeTreinada = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        using var daEquipe = fabrica.Contas.ClienteCom(equipeTreinada.Token);
        var dia = Dia(3);
        await RegistrarManutencaoOkAsync(daEquipe, maquina.Id, dia.AddHours(10), dia.AddHours(12));

        // Sem motivo: o 409 simples, com a mensagem da manutenção. Com motivo: a manutenção recusa antes de tudo, mesmo
        // sem nenhuma reserva em conflito (a desmarcação só rodava quando havia conflitos de reserva).
        using var semMotivo = await LerConflitoAsync(await ReservarAsync(
            daEquipe, maquina.Id, dia.AddHours(11), dia.AddHours(13), "DemandaDoParque", "Prof. Teste"));
        using var comMotivo = await LerConflitoAsync(await ReservarAsync(
            daEquipe, maquina.Id, dia.AddHours(11), dia.AddHours(13), "DemandaDoParque", "Prof. Teste", motivoDesmarcacao: Motivo));

        Assert.Equal(MensagemDaManutencao, Detalhe(semMotivo));
        Assert.Equal("Uma manutenção não se desmarca.", Detalhe(comMotivo));
        foreach (var corpo in new[] { semMotivo, comMotivo })
        {
            Assert.Equal(1, corpo.RootElement.GetProperty("manutencoes").GetArrayLength());
            Assert.Equal(0, corpo.RootElement.GetProperty("conflitos").GetArrayLength());
        }
    }

    [Fact]
    public async Task Reservas_encostadas_na_manutencao_ou_por_cima_de_uma_cancelada_sao_aceitas()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var equipeTreinada = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        using var doDono = fabrica.Contas.ClienteCom(dono.Token);
        using var daEquipe = fabrica.Contas.ClienteCom(equipeTreinada.Token);
        var dia = Dia(3);
        await RegistrarManutencaoOkAsync(daEquipe, maquina.Id, dia.AddHours(10), dia.AddHours(12));
        var cancelada = await RegistrarManutencaoOkAsync(daEquipe, maquina.Id, dia.AddHours(14), dia.AddHours(16));
        Assert.Equal(HttpStatusCode.OK, (await EncerrarManutencaoAsync(daEquipe, cancelada.Id)).StatusCode);

        // Acaba no início da manutenção, começa no fim dela e, por cima da cancelada, uma pessoal e uma demanda.
        await ReservarOkAsync(doDono, maquina.Id, dia.AddHours(9), dia.AddHours(10));
        await ReservarOkAsync(doDono, maquina.Id, dia.AddHours(12), dia.AddHours(13));
        await ReservarOkAsync(daEquipe, maquina.Id, dia.AddHours(14), dia.AddHours(15), "DemandaDoParque", "Prof. Teste");
        await ReservarOkAsync(daEquipe, maquina.Id, dia.AddHours(15), dia.AddHours(16), "DemandaDoParque", "Prof. Teste");
    }

    [Fact]
    public async Task O_409_de_agenda_sem_manutencao_traz_as_tres_listas_com_manutencoes_vazia()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var outra = await _reservas.CriarMaquinaAsync(tipo.Id);
        var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var outroDono = await _reservas.PessoaTreinadaAsync(tipo.Id);
        using var doDono = fabrica.Contas.ClienteCom(dono.Token);
        using var doOutro = fabrica.Contas.ClienteCom(outroDono.Token);
        var dia = Dia(3);
        await ReservarOkAsync(doDono, maquina.Id, dia.AddHours(10), dia.AddHours(11));

        using var corpo = await LerConflitoAsync(await ReservarAsync(doOutro, maquina.Id, dia.AddHours(10), dia.AddHours(11)));

        Assert.Equal(MensagemDoConflito, Detalhe(corpo));
        Assert.Equal(1, corpo.RootElement.GetProperty("conflitos").GetArrayLength());
        Assert.Equal([outra.Id], Ids(corpo.RootElement.GetProperty("maquinasLivres"), "id"));
        Assert.Equal(JsonValueKind.Array, corpo.RootElement.GetProperty("manutencoes").ValueKind);
        Assert.Equal(0, corpo.RootElement.GetProperty("manutencoes").GetArrayLength());
    }

    // ----- A trava 3 da desmarcação (item 5) -----

    [Fact]
    public async Task Demanda_com_motivo_sobre_uma_manutencao_e_uma_pessoal_recusa_e_nao_cancela_nem_a_pessoal()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var equipeTreinada = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        using var doDono = fabrica.Contas.ClienteCom(dono.Token);
        using var daEquipe = fabrica.Contas.ClienteCom(equipeTreinada.Token);
        var dia = Dia(3);
        // A pessoal [10h, 11h) e a manutenção [11h, 13h) estão encostadas; a demanda [10h, 12h) cruza as duas.
        var pessoal = await ReservarOkAsync(doDono, maquina.Id, dia.AddHours(10), dia.AddHours(11));
        await RegistrarManutencaoOkAsync(daEquipe, maquina.Id, dia.AddHours(11), dia.AddHours(13));

        using var comMotivo = await LerConflitoAsync(await ReservarAsync(
            daEquipe, maquina.Id, dia.AddHours(10), dia.AddHours(12), "DemandaDoParque", "Prof. Teste", motivoDesmarcacao: Motivo));
        using var semMotivo = await LerConflitoAsync(await ReservarAsync(
            daEquipe, maquina.Id, dia.AddHours(10), dia.AddHours(12), "DemandaDoParque", "Prof. Teste"));

        Assert.Equal("Uma manutenção não se desmarca.", Detalhe(comMotivo));
        Assert.Equal(MensagemDaManutencao, Detalhe(semMotivo));
        // O 409 traz a pessoal em conflito e a manutenção.
        Assert.Equal([pessoal.Id], Ids(comMotivo.RootElement.GetProperty("conflitos"), "reservaId"));
        Assert.Equal(1, comMotivo.RootElement.GetProperty("manutencoes").GetArrayLength());
        // Nada foi cancelado, nem a pessoal do mesmo intervalo.
        Assert.Equal(SituacaoDaReserva.Ativa, (await VerReservaAsync(dono, pessoal.Id)).Situacao);
    }

    // ----- As máquinas livres (item 6) -----

    [Fact]
    public async Task A_maquina_em_manutencao_sai_da_disponibilidade_e_volta_com_a_manutencao_encostada_ou_cancelada()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var emManutencao = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "B");
        var livre = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "A");
        using var equipe = await _reservas.ClienteDaEquipeAsync();
        var dia = Dia(3);
        var manutencao = await RegistrarManutencaoOkAsync(equipe, emManutencao.Id, dia.AddHours(10), dia.AddHours(12));

        async Task<List<Guid>> LivresAsync(DateTimeOffset inicio, DateTimeOffset fim)
        {
            using var corpo = await LerAsync(equipe, RotaDaDisponibilidade(tipo.Id, inicio, fim));
            return Ids(corpo.RootElement, "id");
        }

        Assert.Equal([livre.Id], await LivresAsync(dia.AddHours(11), dia.AddHours(13)));
        Assert.Equal([livre.Id], await LivresAsync(dia.AddHours(9), dia.AddHours(10).AddMinutes(1)));
        // Encostada na manutenção, antes e depois: volta.
        Assert.Equal([livre.Id, emManutencao.Id], await LivresAsync(dia.AddHours(8), dia.AddHours(10)));
        Assert.Equal([livre.Id, emManutencao.Id], await LivresAsync(dia.AddHours(12), dia.AddHours(14)));

        // Cancelada (ainda não tinha começado): volta.
        Assert.Equal(HttpStatusCode.OK, (await EncerrarManutencaoAsync(equipe, manutencao.Id)).StatusCode);
        Assert.Equal([livre.Id, emManutencao.Id], await LivresAsync(dia.AddHours(11), dia.AddHours(13)));
    }

    [Fact]
    public async Task A_lista_maquinasLivres_do_409_nao_traz_a_maquina_em_manutencao()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var pedida = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "A");
        var emManutencao = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "B");
        var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var outroDono = await _reservas.PessoaTreinadaAsync(tipo.Id);
        using var equipe = await _reservas.ClienteDaEquipeAsync();
        using var doDono = fabrica.Contas.ClienteCom(dono.Token);
        using var doOutro = fabrica.Contas.ClienteCom(outroDono.Token);
        var dia = Dia(3);
        await ReservarOkAsync(doDono, pedida.Id, dia.AddHours(10), dia.AddHours(11));
        var manutencao = await RegistrarManutencaoOkAsync(equipe, emManutencao.Id, dia.AddHours(10), dia.AddHours(12));

        using var antes = await LerConflitoAsync(await ReservarAsync(doOutro, pedida.Id, dia.AddHours(10), dia.AddHours(11)));
        Assert.Equal(HttpStatusCode.OK, (await EncerrarManutencaoAsync(equipe, manutencao.Id)).StatusCode);
        using var depois = await LerConflitoAsync(await ReservarAsync(doOutro, pedida.Id, dia.AddHours(10), dia.AddHours(11)));

        Assert.Equal(0, antes.RootElement.GetProperty("maquinasLivres").GetArrayLength());
        Assert.Equal([emManutencao.Id], Ids(depois.RootElement.GetProperty("maquinasLivres"), "id"));
    }

    [Fact]
    public async Task A_desmarcacao_e_aceita_quando_a_outra_maquina_do_tipo_esta_em_manutencao()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "A");
        var outra = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "B");
        var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var equipeTreinada = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        using var doDono = fabrica.Contas.ClienteCom(dono.Token);
        using var daEquipe = fabrica.Contas.ClienteCom(equipeTreinada.Token);
        var dia = Dia(3);
        var pessoal = await ReservarOkAsync(doDono, maquina.Id, dia.AddHours(10), dia.AddHours(11));

        // Com a outra máquina livre, a trava 2 recusa.
        using var recusada = await LerConflitoAsync(await ReservarAsync(
            daEquipe, maquina.Id, dia.AddHours(10), dia.AddHours(11), "DemandaDoParque", "Prof. Teste", motivoDesmarcacao: Motivo));
        Assert.Contains("está livre nesse horário", Detalhe(recusada));

        // Com ela em manutenção, não há outra máquina livre: a desmarcação passa.
        await RegistrarManutencaoOkAsync(daEquipe, outra.Id, dia.AddHours(9), dia.AddHours(12));
        await ReservarOkAsyncComMotivo(daEquipe, maquina.Id, dia.AddHours(10), dia.AddHours(11));

        Assert.Equal(SituacaoDaReserva.CanceladaPelaEquipe, (await VerReservaAsync(dono, pessoal.Id)).Situacao);
    }

    private static async Task ReservarOkAsyncComMotivo(HttpClient cliente, Guid maquinaId, DateTimeOffset inicio, DateTimeOffset fim)
    {
        var resposta = await ReservarAsync(
            cliente, maquinaId, inicio, fim, "DemandaDoParque", "Prof. Teste", motivoDesmarcacao: Motivo);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }
}
