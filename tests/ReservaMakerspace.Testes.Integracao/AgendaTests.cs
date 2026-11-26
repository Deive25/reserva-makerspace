using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// <c>GET /equipamentos/{id}/agenda</c> e a regra de quem vê o quê (plano do 1.6, decisões 1 a 3; itens 3 e 13 do
/// Foco). Cada teste cria o próprio tipo, porque a coleção compartilha o banco.
/// </summary>
[Collection(ApiCollection.Nome)]
public class AgendaTests(FabricaDaApi fabrica)
{
    private readonly AuxiliarDeReservas _reservas = new(fabrica.Contas);

    private static string Rota(Guid maquinaId, DateTimeOffset de, DateTimeOffset ate) =>
        $"/equipamentos/{maquinaId}/agenda?de={Horario(de)}&ate={Horario(ate)}";

    private static async Task<AgendaResposta> LerAgendaAsync(HttpClient cliente, string rota)
    {
        var resposta = await cliente.GetAsync(rota, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return await Json.LerAsync<AgendaResposta>(resposta);
    }

    private async Task<(TipoDeEquipamentoResposta Tipo, EquipamentoResposta Maquina, ContaDeTeste Equipe)> MontarAsync()
    {
        var tipo = await _reservas.CriarTipoAsync(minutosMaximos: 600);
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var equipe = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        return (tipo, maquina, equipe);
    }

    [Fact]
    public async Task Traz_as_reservas_que_cruzam_a_janela_em_ordem_de_inicio()
    {
        var (tipo, maquina, equipe) = await MontarAsync();
        var outraMaquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var dia = Dia(3);
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        using var doDono = fabrica.Contas.ClienteCom(dono.Token);

        // A janela é [10h, 14h). Nasce fora da ordem de início.
        var depois = await ReservarOkAsync(daEquipe, maquina.Id, dia.AddHours(13), dia.AddHours(15), "DemandaDoParque");
        var dentro = await ReservarOkAsync(daEquipe, maquina.Id, dia.AddHours(11), dia.AddHours(12), "DemandaDoParque");
        var antes = await ReservarOkAsync(daEquipe, maquina.Id, dia.AddHours(8), dia.AddHours(10).AddMinutes(30), "DemandaDoParque");
        var cancelada = await ReservarOkAsync(doDono, maquina.Id, dia.AddHours(12), dia.AddHours(13));
        Assert.Equal(HttpStatusCode.OK, (await CancelarAsync(doDono, cancelada.Id)).StatusCode);
        await ReservarOkAsync(daEquipe, outraMaquina.Id, dia.AddHours(11), dia.AddHours(12), "DemandaDoParque");

        var agenda = await LerAgendaAsync(daEquipe, Rota(maquina.Id, dia.AddHours(10), dia.AddHours(14)));

        Assert.Equal(maquina.Id, agenda.EquipamentoId);
        Assert.Equal(maquina.Nome, agenda.EquipamentoNome);
        Assert.Equal(dia.AddHours(10), agenda.De);
        Assert.Equal(dia.AddHours(14), agenda.Ate);
        // A que começou antes de "de" e a que acaba depois de "ate" entram; a cancelada e a de outra máquina, não.
        Assert.Equal(
            [antes.Id, dentro.Id, depois.Id],
            agenda.Reservas.Select(r => r.ReservaId!.Value));
        Assert.Equal(dia.AddHours(8), agenda.Reservas[0].Inicio);
        Assert.Equal(dia.AddHours(15), agenda.Reservas[2].Fim);
    }

    [Fact]
    public async Task Reservas_encostadas_nas_bordas_ficam_de_fora()
    {
        var (_, maquina, equipe) = await MontarAsync();
        var dia = Dia(3);
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        await ReservarOkAsync(daEquipe, maquina.Id, dia.AddHours(8), dia.AddHours(10), "DemandaDoParque");
        await ReservarOkAsync(daEquipe, maquina.Id, dia.AddHours(14), dia.AddHours(16), "DemandaDoParque");

        var agenda = await LerAgendaAsync(daEquipe, Rota(maquina.Id, dia.AddHours(10), dia.AddHours(14)));

        Assert.Empty(agenda.Reservas);
    }

    [Fact]
    public async Task Maquina_inativa_tambem_tem_agenda()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var inativa = await _reservas.CriarMaquinaAsync(tipo.Id, ativo: false);
        using var cliente = fabrica.Contas.ClienteCom((await fabrica.Contas.CriarAsync(Papel.Usuario)).Token);
        var dia = Dia(3);

        var agenda = await LerAgendaAsync(cliente, Rota(inativa.Id, dia, dia.AddDays(1)));

        Assert.Equal(inativa.Id, agenda.EquipamentoId);
        Assert.Empty(agenda.Reservas);
    }

    [Fact]
    public async Task Janela_de_exatamente_31_dias_responde_200_e_um_segundo_a_mais_responde_400()
    {
        var (_, maquina, _) = await MontarAsync();
        using var cliente = fabrica.Contas.ClienteCom((await fabrica.Contas.CriarAsync(Papel.Usuario)).Token);
        var de = Dia(3);

        await LerAgendaAsync(cliente, Rota(maquina.Id, de, de.AddDays(31)));

        var resposta = await cliente.GetAsync(
            Rota(maquina.Id, de, de.AddDays(31).AddSeconds(1)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.True(corpo.RootElement.GetProperty("errors").TryGetProperty("ate", out _));
    }

    [Theory]
    [InlineData("", "de")]
    [InlineData("ate=2030-01-02T10:00:00Z", "de")]
    [InlineData("de=2030-01-01T10:00:00Z", "ate")]
    [InlineData("de=2030-01-01T10:00:00&ate=2030-01-02T10:00:00Z", "de")]
    [InlineData("de=2030-01-01T10:00:00Z&ate=2030-01-02T10:00:00", "ate")]
    [InlineData("de=2030-01-01T10:00:00%2003:00&ate=2030-01-02T10:00:00Z", "de")]
    [InlineData("de=2030-01-01T10:00:00Z&ate=2030-01-01T10:00:00Z", "ate")]
    [InlineData("de=2030-01-02T10:00:00Z&ate=2030-01-01T10:00:00Z", "ate")]
    public async Task Parametro_ausente_sem_fuso_ou_invertido_responde_400_no_campo(string query, string campo)
    {
        var (_, maquina, _) = await MontarAsync();
        using var cliente = fabrica.Contas.ClienteCom((await fabrica.Contas.CriarAsync(Papel.Usuario)).Token);

        var resposta = await cliente.GetAsync(
            $"/equipamentos/{maquina.Id}/agenda?{query}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.True(
            corpo.RootElement.GetProperty("errors").TryGetProperty(campo, out _),
            $"Sem erro em '{campo}': {corpo.RootElement}");
    }

    [Fact]
    public async Task Maquina_inexistente_responde_404()
    {
        using var cliente = fabrica.Contas.ClienteCom((await fabrica.Contas.CriarAsync(Papel.Usuario)).Token);
        var de = Dia(3);

        var resposta = await cliente.GetAsync(
            Rota(Guid.NewGuid(), de, de.AddDays(1)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    private static bool Tem(JsonElement reserva, string campo) =>
        reserva.TryGetProperty(campo, out var valor) && valor.ValueKind != JsonValueKind.Null;

    [Fact]
    public async Task O_Usuario_so_ve_o_id_e_o_nome_nas_proprias_reservas_e_a_Equipe_ve_em_todas()
    {
        var (tipo, maquina, equipe) = await MontarAsync();
        var ana = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var bia = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var dia = Dia(3);
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        using var daAna = fabrica.Contas.ClienteCom(ana.Token);
        using var daBia = fabrica.Contas.ClienteCom(bia.Token);
        var deAna = await ReservarOkAsync(daAna, maquina.Id, dia.AddHours(9), dia.AddHours(10));
        var deBia = await ReservarOkAsync(daBia, maquina.Id, dia.AddHours(10), dia.AddHours(11));
        var demanda = await ReservarOkAsync(
            daEquipe, maquina.Id, dia.AddHours(11), dia.AddHours(12), "DemandaDoParque", "Prof. Daniel");
        var rota = Rota(maquina.Id, dia.AddHours(8), dia.AddHours(13));

        // A Ana: tudo nas dela; nas dos outros, só o horário e a finalidade.
        var respostaDaAna = await daAna.GetAsync(rota, TestContext.Current.CancellationToken);
        using (var corpo = await Json.DocumentoAsync(respostaDaAna))
        {
            var reservas = corpo.RootElement.GetProperty("reservas").EnumerateArray().ToList();
            Assert.Equal(3, reservas.Count);
            Assert.Equal(deAna.Id, reservas[0].GetProperty("reservaId").GetGuid());
            Assert.Equal("Pessoa de Teste", reservas[0].GetProperty("operadorNome").GetString());
            foreach (var dosOutros in reservas.Skip(1))
            {
                Assert.False(Tem(dosOutros, "reservaId"));
                Assert.False(Tem(dosOutros, "operadorNome"));
                Assert.False(Tem(dosOutros, "solicitante"));
                Assert.True(Tem(dosOutros, "inicio") && Tem(dosOutros, "fim") && Tem(dosOutros, "finalidade"));
            }

            Assert.DoesNotContain("Prof. Daniel", corpo.RootElement.ToString());
        }

        // A Equipe vê o id, o nome e o solicitante de todas.
        var daEquipeAgenda = await LerAgendaAsync(daEquipe, rota);
        Assert.Equal(
            [deAna.Id, deBia.Id, demanda.Id],
            daEquipeAgenda.Reservas.Select(r => r.ReservaId!.Value));
        Assert.All(daEquipeAgenda.Reservas, r => Assert.Equal("Pessoa de Teste", r.OperadorNome));
        Assert.Equal("Prof. Daniel", daEquipeAgenda.Reservas[2].Solicitante);

        // A Coordenação também é Equipe.
        using var daCoordenacao = await _reservas.ClienteDaCoordenacaoAsync();
        var daCoordenacaoAgenda = await LerAgendaAsync(daCoordenacao, rota);
        Assert.All(daCoordenacaoAgenda.Reservas, r => Assert.NotNull(r.OperadorNome));
    }

    [Fact]
    public async Task Os_conflitos_do_409_seguem_a_mesma_regra_dos_nomes()
    {
        var (tipo, maquina, equipe) = await MontarAsync();
        var ana = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var bia = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var dia = Dia(3);
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        using var daAna = fabrica.Contas.ClienteCom(ana.Token);
        using var daBia = fabrica.Contas.ClienteCom(bia.Token);
        var deAna = await ReservarOkAsync(daAna, maquina.Id, dia.AddHours(9), dia.AddHours(10));

        // A Bia bate na reserva da Ana: o 409 não mostra quem é.
        var daBiaResposta = await ReservarAsync(daBia, maquina.Id, dia.AddHours(9), dia.AddHours(10));
        Assert.Equal(HttpStatusCode.Conflict, daBiaResposta.StatusCode);
        using (var corpo = await Json.DocumentoAsync(daBiaResposta))
        {
            var conflito = Assert.Single(corpo.RootElement.GetProperty("conflitos").EnumerateArray());
            Assert.False(Tem(conflito, "reservaId"));
            Assert.False(Tem(conflito, "operadorNome"));
            Assert.False(Tem(conflito, "solicitante"));
        }

        // A Ana, no horário dela mesma: o 409 mostra a reserva dela.
        var daAnaResposta = await ReservarAsync(daAna, maquina.Id, dia.AddHours(9), dia.AddHours(10));
        using (var corpo = await Json.DocumentoAsync(daAnaResposta))
        {
            var conflito = Assert.Single(corpo.RootElement.GetProperty("conflitos").EnumerateArray());
            Assert.Equal(deAna.Id, conflito.GetProperty("reservaId").GetGuid());
        }

        // A Equipe vê o id e o nome.
        var daEquipeResposta = await ReservarAsync(daEquipe, maquina.Id, dia.AddHours(9), dia.AddHours(10), "DemandaDoParque");
        using (var corpo = await Json.DocumentoAsync(daEquipeResposta))
        {
            var conflito = Assert.Single(corpo.RootElement.GetProperty("conflitos").EnumerateArray());
            Assert.Equal(deAna.Id, conflito.GetProperty("reservaId").GetGuid());
            Assert.Equal("Pessoa de Teste", conflito.GetProperty("operadorNome").GetString());
        }
    }

    [Fact]
    public async Task O_409_do_PUT_de_equipamentos_traz_o_id_e_o_nome_do_operador()
    {
        var (_, maquina, equipe) = await MontarAsync();
        var dia = Dia(3);
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        var demanda = await ReservarOkAsync(daEquipe, maquina.Id, dia.AddHours(9), dia.AddHours(10), "DemandaDoParque", "Prof. Daniel");

        using var daCoordenacao = await _reservas.ClienteDaCoordenacaoAsync();
        var resposta = await daCoordenacao.PutAsJsonAsync(
            $"/equipamentos/{maquina.Id}",
            new { nome = maquina.Nome, tipoId = maquina.TipoId, ativo = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        var conflito = Assert.Single(corpo.RootElement.GetProperty("conflitos").EnumerateArray());
        Assert.Equal(demanda.Id, conflito.GetProperty("reservaId").GetGuid());
        Assert.Equal("Pessoa de Teste", conflito.GetProperty("operadorNome").GetString());
        Assert.Equal("Prof. Daniel", conflito.GetProperty("solicitante").GetString());
    }
}
