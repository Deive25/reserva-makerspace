using System.Net;
using Microsoft.Extensions.Time.Testing;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// A desmarcação com o relógio falso (as bordas das 24 h, trava 1) e a transação única (decisão 6; itens 4 e 10 do
/// Foco). Cada teste sobe a API num banco próprio e junta vários passos, porque isso custa mais do que um teste do banco
/// compartilhado.
/// </summary>
public class DesmarcacaoComRelogioTests(BancosDeTeste bancos) : IClassFixture<BancosDeTeste>
{
    private static readonly DateTimeOffset T0 = new(2027, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private const string Motivo = "Visita técnica do parque.";

    private const string MensagemDas24Horas = "Só dá para desmarcar reservas que começam daqui a mais de 24 h.";

    /// <summary>Um tipo de uma máquina só (a trava 2 não interfere), uma pessoa da Equipe e o dono de uma pessoal.</summary>
    private sealed record Caso(
        ContaDeTeste Dono, ContaDeTeste Equipe, Guid MaquinaId, ReservaResposta Pessoal);

    private static async Task<Caso> NovoCasoAsync(AuxiliarDeReservas auxiliar, DateTimeOffset inicioDaPessoal)
    {
        var tipo = await auxiliar.CriarTipoAsync();
        var maquina = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var dono = await auxiliar.PessoaTreinadaAsync(tipo.Id);
        var equipe = await auxiliar.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        using var cliente = await auxiliar.ClienteAsync(dono);
        var pessoal = await ReservarOkAsync(cliente, maquina.Id, inicioDaPessoal, inicioDaPessoal.AddHours(1));
        return new Caso(dono, equipe, maquina.Id, pessoal);
    }

    private static async Task<HttpResponseMessage> PedirDemandaAsync(
        AuxiliarDeReservas auxiliar, Caso caso, DateTimeOffset inicio)
    {
        using var cliente = await auxiliar.ClienteAsync(caso.Equipe);
        return await ReservarAsync(
            cliente, caso.MaquinaId, inicio, inicio.AddHours(1), "DemandaDoParque", "Prof. Teste", motivoDesmarcacao: Motivo);
    }

    private static async Task<ReservaResposta> VerAsync(AuxiliarDeReservas auxiliar, Caso caso)
    {
        using var cliente = await auxiliar.ClienteAsync(caso.Dono);
        return Assert.Single(await ListarAsync(cliente, "/me/reservas"), r => r.Id == caso.Pessoal.Id);
    }

    [Fact]
    public async Task Bordas_das_24_horas_desmarca_com_24_h_e_1_s_e_recusa_com_exatamente_24_h_ou_em_andamento()
    {
        await using var fabrica = new FabricaComRelogio(await bancos.NovoBancoAsync(), new FakeTimeProvider(T0));
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);

        // Todos os casos nascem em T0. O relógio só anda para a frente, e por isso a borda que desmarca (24 h + 1 s)
        // vem antes da que recusa (exatamente 24 h), e o caso em andamento vem por último.
        var passa = await NovoCasoAsync(auxiliar, T0.AddHours(24).AddSeconds(1));
        var exatas = await NovoCasoAsync(auxiliar, T0.AddHours(24));
        var umSegundoAntes = await NovoCasoAsync(auxiliar, T0.AddHours(24).AddSeconds(-1));
        var emAndamento = await NovoCasoAsync(auxiliar, T0.AddHours(1));

        // 24 h + 1 s: desmarca. A data do cancelamento vem do relógio falso.
        var resposta = await PedirDemandaAsync(auxiliar, passa, passa.Pessoal.Inicio);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var cancelada = await VerAsync(auxiliar, passa);
        Assert.Equal(SituacaoDaReserva.CanceladaPelaEquipe, cancelada.Situacao);
        Assert.Equal(Motivo, cancelada.MotivoDoCancelamento);
        Assert.Equal(T0, cancelada.CanceladaEm);

        // Exatamente 24 h, e 1 s a menos: recusa, e a pessoal continua ativa.
        foreach (var recusado in new[] { exatas, umSegundoAntes })
        {
            var recusa = await PedirDemandaAsync(auxiliar, recusado, recusado.Pessoal.Inicio);
            await ConferirRecusaAsync(recusa);
            Assert.Equal(SituacaoDaReserva.Ativa, (await VerAsync(auxiliar, recusado)).Situacao);
        }

        // Em andamento: o relógio chega à metade da reserva (o início no passado também recusa).
        fabrica.Relogio.Advance(TimeSpan.FromMinutes(90));
        var agora = fabrica.Relogio.GetUtcNow();
        Assert.True(emAndamento.Pessoal.Inicio < agora && agora < emAndamento.Pessoal.Fim);
        await ConferirRecusaAsync(await PedirDemandaAsync(auxiliar, emAndamento, agora));
        Assert.Equal(SituacaoDaReserva.Ativa, (await VerAsync(auxiliar, emAndamento)).Situacao);
    }

    private static async Task ConferirRecusaAsync(HttpResponseMessage resposta)
    {
        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal(MensagemDas24Horas, corpo.RootElement.GetProperty("detail").GetString());
        Assert.Equal(1, corpo.RootElement.GetProperty("conflitos").GetArrayLength());
    }

    [Fact]
    public async Task Falha_no_INSERT_da_demanda_desfaz_a_desmarcacao_e_a_pessoal_continua_ativa()
    {
        await using var fabrica = new FabricaComFalhaNoInsert(await bancos.NovoBancoAsync());
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        var tipo = await auxiliar.CriarTipoAsync();
        var maquina = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var dono = await auxiliar.PessoaTreinadaAsync(tipo.Id);
        var equipe = await auxiliar.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        var inicio = Dia(2).AddHours(10);
        using var doDono = fabrica.Contas.ClienteCom(dono.Token);
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        var pessoal = await ReservarOkAsync(doDono, maquina.Id, inicio, inicio.AddHours(1));

        // Montado o cenário, a falha é armada: o UPDATE da desmarcação roda e o INSERT da demanda falha.
        fabrica.Falha.Armar();
        var falhou = await ReservarAsync(
            daEquipe, maquina.Id, inicio, inicio.AddHours(1), "DemandaDoParque", "Prof. Teste", motivoDesmarcacao: Motivo);

        Assert.Equal(HttpStatusCode.InternalServerError, falhou.StatusCode);
        var depoisDaFalha = Assert.Single(await ListarAsync(doDono, "/me/reservas"), r => r.Id == pessoal.Id);
        Assert.Equal(SituacaoDaReserva.Ativa, depoisDaFalha.Situacao);
        Assert.Null(depoisDaFalha.MotivoDoCancelamento);
        Assert.Null(depoisDaFalha.CanceladaEm);
        Assert.Equal(
            0,
            await BancosDeTeste.ContarAsync(
                fabrica.ConnectionString, "SELECT count(*) FROM reservas WHERE finalidade = 'DemandaDoParque'"));

        // Controle: sem a falha, o mesmo pedido desmarca e cria a demanda. A falha era a única coisa no caminho.
        fabrica.Falha.Desarmar();
        var aceita = await ReservarAsync(
            daEquipe, maquina.Id, inicio, inicio.AddHours(1), "DemandaDoParque", "Prof. Teste", motivoDesmarcacao: Motivo);
        Assert.Equal(HttpStatusCode.Created, aceita.StatusCode);
        var depois = Assert.Single(await ListarAsync(doDono, "/me/reservas"), r => r.Id == pessoal.Id);
        Assert.Equal(SituacaoDaReserva.CanceladaPelaEquipe, depois.Situacao);
    }
}
