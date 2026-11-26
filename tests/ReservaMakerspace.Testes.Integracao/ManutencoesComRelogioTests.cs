using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ReservaMakerspace.Contratos.Manutencoes;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// A manutenção com o relógio falso (plano do 1.7, decisões 2 e 4; itens 2, 3, 7 e 9 do Foco) e a transação única do
/// registro (com o interceptador). Cada teste sobe a API
/// num banco próprio e junta vários passos, porque isso custa mais do que um teste do banco compartilhado. O relógio só
/// anda para a frente: os casos nascem todos no mesmo instante e são conferidos na ordem do tempo.
/// </summary>
public class ManutencoesComRelogioTests(BancosDeTeste bancos) : IClassFixture<BancosDeTeste>
{
    private static readonly DateTimeOffset T0 = new(2027, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static async Task<ManutencaoResposta> LerManutencaoAsync(HttpResponseMessage resposta)
    {
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return await Json.LerAsync<ManutencaoResposta>(resposta);
    }

    private static async Task<string> DetalheAsync(HttpResponseMessage resposta)
    {
        using var corpo = await Json.DocumentoAsync(resposta);
        return corpo.RootElement.GetProperty("detail").GetString()!;
    }

    private static async Task<ReservaResposta> VerReservaAsync(AuxiliarDeReservas auxiliar, ContaDeTeste dono, Guid reservaId)
    {
        using var cliente = await auxiliar.ClienteAsync(dono);
        return Assert.Single(await ListarAsync(cliente, "/me/reservas"), r => r.Id == reservaId);
    }

    [Fact]
    public async Task O_inicio_nao_pode_ser_antes_do_agora_e_sem_inicio_a_manutencao_cancela_a_reserva_em_andamento()
    {
        await using var fabrica = new FabricaComRelogio(await bancos.NovoBancoAsync(), new FakeTimeProvider(T0));
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        var tipo = await auxiliar.CriarTipoAsync();
        var maquina = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var outra = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var dono = await auxiliar.PessoaTreinadaAsync(tipo.Id);
        var equipeTreinada = await auxiliar.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);

        // A pessoal [T0, T0+1h) vai estar em andamento; a demanda [T0+1h, T0+2h) ainda não começou.
        using var doDono = await auxiliar.ClienteAsync(dono);
        var emAndamento = await ReservarOkAsync(doDono, maquina.Id, T0, T0.AddHours(1));
        using var daEquipeTreinada = await auxiliar.ClienteAsync(equipeTreinada);
        var futura = await ReservarOkAsync(daEquipeTreinada, maquina.Id, T0.AddHours(1), T0.AddHours(2), "DemandaDoParque", "Prof. Teste");

        fabrica.Relogio.Advance(TimeSpan.FromMinutes(30));
        var agora = fabrica.Relogio.GetUtcNow();
        Assert.True(emAndamento.Inicio < agora && agora < emAndamento.Fim);

        // Um segundo antes do agora: 422. É a regra do início, e vem antes da agenda (409), mesmo com a máquina já parada.
        using var equipe = await auxiliar.ClienteDaEquipeAsync();
        var noPassado = await RegistrarManutencaoAsync(equipe, outra.Id, agora.AddSeconds(-1), agora.AddHours(2));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noPassado.StatusCode);
        Assert.Equal("O início da manutenção não pode ser no passado.", await DetalheAsync(noPassado));

        // Exatamente no agora: aceito.
        var noAgora = await RegistrarManutencaoOkAsync(equipe, outra.Id, agora, agora.AddHours(2));
        Assert.Equal(agora, noAgora.Inicio);

        var noPassadoComAgendaCheia = await RegistrarManutencaoAsync(equipe, outra.Id, agora.AddSeconds(-1), agora.AddHours(2));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noPassadoComAgendaCheia.StatusCode);

        // Sem início: começa no agora do servidor, e cancela a reserva em andamento e a que ainda não começou.
        var semInicio = await RegistrarManutencaoOkAsync(equipe, maquina.Id, inicio: null, agora.AddHours(3), "Quebrou o bico.");
        Assert.Equal(agora, semInicio.Inicio);
        Assert.Equal(agora, semInicio.RegistradaEm);

        var cancelada = await VerReservaAsync(auxiliar, dono, emAndamento.Id);
        Assert.Equal(SituacaoDaReserva.CanceladaPorManutencao, cancelada.Situacao);
        Assert.Equal("Quebrou o bico.", cancelada.MotivoDoCancelamento);
        Assert.Equal(agora, cancelada.CanceladaEm);
        Assert.Equal("Pessoa de Teste", cancelada.CanceladaPorNome);

        var daDemanda = await VerReservaAsync(auxiliar, equipeTreinada, futura.Id);
        Assert.Equal(SituacaoDaReserva.CanceladaPorManutencao, daDemanda.Situacao);
        Assert.Equal(agora, daDemanda.CanceladaEm);
    }

    [Fact]
    public async Task Encerrar_decide_pelo_relogio_entre_encurtar_cancelar_e_recusar()
    {
        await using var fabrica = new FabricaComRelogio(await bancos.NovoBancoAsync(), new FakeTimeProvider(T0));
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        var tipo = await auxiliar.CriarTipoAsync();
        var maquinaA = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var maquinaB = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var maquinaC = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var maquinaD = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var donoB = await auxiliar.PessoaTreinadaAsync(tipo.Id);
        var novoDono = await auxiliar.PessoaTreinadaAsync(tipo.Id);
        var equipeTreinada = await auxiliar.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        using var registrou = await auxiliar.ClienteDaEquipeAsync();

        // Todas nascem em T0, e cada uma tem a sua máquina. A reserva da B, que a manutenção cancela, nasce antes.
        using var doDonoB = await auxiliar.ClienteAsync(donoB);
        var reservaDaB = await ReservarOkAsync(doDonoB, maquinaB.Id, T0.AddHours(1), T0.AddHours(2));
        var a = await RegistrarManutencaoOkAsync(registrou, maquinaA.Id, T0.AddHours(1), T0.AddHours(4));
        var b = await RegistrarManutencaoOkAsync(registrou, maquinaB.Id, T0.AddHours(1), T0.AddHours(5));
        var c = await RegistrarManutencaoOkAsync(registrou, maquinaC.Id, T0.AddHours(1), T0.AddHours(3));
        var d = await RegistrarManutencaoOkAsync(registrou, maquinaD.Id, T0.AddHours(2), T0.AddHours(4));
        Assert.Equal(SituacaoDaReserva.CanceladaPorManutencao, (await VerReservaAsync(auxiliar, donoB, reservaDaB.Id)).Situacao);

        // T0+1h. A que começa exatamente agora conta como "ainda não começou": cancela, e o intervalo fica como foi registrado.
        fabrica.Relogio.Advance(TimeSpan.FromHours(1));
        // O token dura 60 minutos e o relógio anda horas: cada passo entra de novo.
        async Task<HttpResponseMessage> EncerrarAsync(Guid id)
        {
            using var cliente = await auxiliar.ClienteAsync(equipeTreinada);
            return await EncerrarManutencaoAsync(cliente, id);
        }

        var aEncerrada = await LerManutencaoAsync(await EncerrarAsync(a.Id));
        Assert.Equal(SituacaoDaManutencao.Cancelada, aEncerrada.Situacao);
        Assert.Equal(T0.AddHours(1), aEncerrada.Inicio);
        Assert.Equal(T0.AddHours(4), aEncerrada.Fim);
        Assert.Equal(T0.AddHours(1), aEncerrada.EncerradaEm);
        Assert.Equal("Pessoa de Teste", aEncerrada.EncerradaPorNome);

        // A que ainda não começou (começa daqui a 1 h): cancela.
        var dEncerrada = await LerManutencaoAsync(await EncerrarAsync(d.Id));
        Assert.Equal(SituacaoDaManutencao.Cancelada, dEncerrada.Situacao);

        // Já cancelada: 409 e nada muda.
        var denovo = await EncerrarAsync(a.Id);
        Assert.Equal(HttpStatusCode.Conflict, denovo.StatusCode);
        Assert.Equal("A manutenção já foi cancelada.", await DetalheAsync(denovo));

        // T0+2h. Em andamento: o fim passa a ser o agora, e ela continua ativa, com a data e o autor do encerramento.
        fabrica.Relogio.Advance(TimeSpan.FromHours(1));
        var bEncerrada = await LerManutencaoAsync(await EncerrarAsync(b.Id));
        Assert.Equal(SituacaoDaManutencao.Ativa, bEncerrada.Situacao);
        Assert.Equal(T0.AddHours(1), bEncerrada.Inicio);
        Assert.Equal(T0.AddHours(2), bEncerrada.Fim);
        Assert.Equal(T0.AddHours(2), bEncerrada.EncerradaEm);
        Assert.Equal("Pessoa de Teste", bEncerrada.EncerradaPorNome);

        // As reservas que ela cancelou continuam canceladas (§4.4).
        Assert.Equal(SituacaoDaReserva.CanceladaPorManutencao, (await VerReservaAsync(auxiliar, donoB, reservaDaB.Id)).Situacao);

        // A máquina volta a aceitar reservas a partir do fim novo, e também no pedaço que sobrava da manutenção.
        using var doNovoDono = await auxiliar.ClienteAsync(novoDono);
        await ReservarOkAsync(doNovoDono, maquinaB.Id, T0.AddHours(2), T0.AddHours(3));
        await ReservarOkAsync(doNovoDono, maquinaB.Id, T0.AddHours(4), T0.AddHours(5));

        // A agenda mostra o fim novo; a disponibilidade, a máquina livre antes do fim antigo.
        using var daEquipe = await auxiliar.ClienteAsync(equipeTreinada);
        using (var agenda = await LerAsync(daEquipe, $"/equipamentos/{maquinaB.Id}/agenda?de={Horario(T0)}&ate={Horario(T0.AddHours(8))}"))
        {
            var item = Assert.Single(agenda.RootElement.GetProperty("manutencoes").EnumerateArray());
            Assert.Equal(b.Id, item.GetProperty("manutencaoId").GetGuid());
            Assert.Equal(T0.AddHours(2), item.GetProperty("fim").GetDateTimeOffset());
        }

        // T0+3h. Exatamente no fim: já acabou (409). Depois do fim, também.
        fabrica.Relogio.Advance(TimeSpan.FromHours(1));
        var noFim = await EncerrarAsync(c.Id);
        Assert.Equal(HttpStatusCode.Conflict, noFim.StatusCode);
        Assert.Equal("A manutenção já acabou.", await DetalheAsync(noFim));
        fabrica.Relogio.Advance(TimeSpan.FromHours(1));
        var depoisDoFim = await EncerrarAsync(c.Id);
        Assert.Equal(HttpStatusCode.Conflict, depoisDoFim.StatusCode);
        Assert.Equal("A manutenção já acabou.", await DetalheAsync(depoisDoFim));

        // A que já acabou fica como estava: ativa, sem encerramento.
        using var leitura = await auxiliar.ClienteAsync(equipeTreinada);
        var lida = await LerManutencaoAsync(await leitura.GetAsync($"/manutencoes/{c.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(SituacaoDaManutencao.Ativa, lida.Situacao);
        Assert.Equal(T0.AddHours(3), lida.Fim);
        Assert.Null(lida.EncerradaEm);
    }

    [Fact]
    public async Task Falha_ao_gravar_a_manutencao_desfaz_o_cancelamento_das_reservas()
    {
        await using var fabrica = new FabricaComInterceptador(await bancos.NovoBancoAsync());
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        var tipo = await auxiliar.CriarTipoAsync();
        var maquina = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var dono = await auxiliar.PessoaTreinadaAsync(tipo.Id);
        using var equipe = await auxiliar.ClienteDaEquipeAsync();
        var inicio = Dia(2).AddHours(10);
        using var doDono = fabrica.Contas.ClienteCom(dono.Token);
        var pessoal = await ReservarOkAsync(doDono, maquina.Id, inicio, inicio.AddHours(1));

        // Montado o cenário, a falha é armada: o UPDATE que cancela a reserva roda e o INSERT da manutenção falha.
        fabrica.Interceptador.ArmarFalhaNoInsertDeManutencao();
        var falhou = await RegistrarManutencaoAsync(equipe, maquina.Id, inicio, inicio.AddHours(2));

        Assert.Equal(HttpStatusCode.InternalServerError, falhou.StatusCode);
        var depoisDaFalha = Assert.Single(await ListarAsync(doDono, "/me/reservas"), r => r.Id == pessoal.Id);
        Assert.Equal(SituacaoDaReserva.Ativa, depoisDaFalha.Situacao);
        Assert.Null(depoisDaFalha.MotivoDoCancelamento);
        Assert.Null(depoisDaFalha.CanceladaEm);
        Assert.Equal(0, await BancosDeTeste.ContarAsync(fabrica.ConnectionString, "SELECT count(*) FROM manutencoes"));

        // Controle: sem a falha, o mesmo pedido grava a manutenção e cancela a reserva.
        fabrica.Interceptador.DesarmarFalhaNoInsertDeManutencao();
        await RegistrarManutencaoOkAsync(equipe, maquina.Id, inicio, inicio.AddHours(2));
        var depois = Assert.Single(await ListarAsync(doDono, "/me/reservas"), r => r.Id == pessoal.Id);
        Assert.Equal(SituacaoDaReserva.CanceladaPorManutencao, depois.Situacao);
    }

    private static async Task<JsonDocument> LerAsync(HttpClient cliente, string rota)
    {
        var resposta = await cliente.GetAsync(rota, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return await Json.DocumentoAsync(resposta);
    }
}
