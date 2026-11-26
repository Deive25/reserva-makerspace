using System.Net;
using System.Net.Http.Json;
using Npgsql;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// A desmarcação com pedidos simultâneos (plano do 1.6, decisão 7; item 11 do Foco). Cada teste repete várias rodadas,
/// com um cliente por pedido e <see cref="Task.WhenAll"/>, e nenhuma rodada pode terminar em 500. O tipo tem uma
/// máquina só (a trava 2 não interfere), cada rodada usa um horário diferente e um dono novo (o limite é de 2 pessoais
/// ativas por pessoa). O relógio é o do sistema: as pessoais começam daqui a ~2 dias, longe das 24 h.
/// </summary>
[Collection(ApiCollection.Nome)]
public class ConcorrenciaDaDesmarcacaoTests(FabricaDaApi fabrica)
{
    private const int Rodadas = 12;

    private const string Motivo = "Visita técnica do parque.";

    private readonly AuxiliarDeReservas _reservas = new(fabrica.Contas);

    /// <summary>O início da rodada: 6 h depois da meia-noite de daqui a 2 dias, e 3 h a mais a cada rodada.</summary>
    private static DateTimeOffset InicioDaRodada(int rodada) => Dia(2).AddHours(6 + (3 * rodada));

    private static async Task<HttpResponseMessage[]> EmParaleloAsync(params Func<Task<HttpResponseMessage>>[] pedidos) =>
        await Task.WhenAll(pedidos.Select(p => Task.Run(p)));

    private async Task<(string Situacao, string? Motivo)> EstadoAsync(Guid reservaId)
    {
        await using var fonte = NpgsqlDataSource.Create(fabrica.ConnectionString);
        await using var comando = fonte.CreateCommand(
            "SELECT situacao, motivo_do_cancelamento FROM reservas WHERE id = $1");
        comando.Parameters.AddWithValue(reservaId);
        await using var leitor = await comando.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await leitor.ReadAsync(TestContext.Current.CancellationToken));
        return (leitor.GetString(0), await leitor.IsDBNullAsync(1) ? null : leitor.GetString(1));
    }

    private async Task<long> DemandasAtivasAsync(Guid maquinaId) =>
        await BancosDeTeste.ContarAsync(
            fabrica.ConnectionString,
            $"SELECT count(*) FROM reservas WHERE equipamento_id = '{maquinaId}' AND finalidade = 'DemandaDoParque' AND situacao = 'Ativa'");

    private static Task<HttpResponseMessage> PedirDemandaAsync(
        HttpClient cliente, Guid maquinaId, DateTimeOffset inicio, DateTimeOffset fim) =>
        ReservarAsync(cliente, maquinaId, inicio, fim, "DemandaDoParque", "Prof. Teste", motivoDesmarcacao: Motivo);

    [Fact]
    public async Task Duas_demandas_com_motivo_sobre_a_mesma_pessoal_dao_um_201_e_um_409_e_uma_demanda_ativa_so()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var primeira = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        var segunda = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);

        for (var rodada = 0; rodada < Rodadas; rodada++)
        {
            var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
            var inicio = InicioDaRodada(rodada);
            using var doDono = fabrica.Contas.ClienteCom(dono.Token);
            var pessoal = await ReservarOkAsync(doDono, maquina.Id, inicio, inicio.AddHours(2));
            using var umCliente = fabrica.Contas.ClienteCom(primeira.Token);
            using var outroCliente = fabrica.Contas.ClienteCom(segunda.Token);

            // As duas pegam a pessoal, com janelas diferentes: a segunda a encontra já cancelada, mas vê a demanda da primeira.
            var respostas = await EmParaleloAsync(
                () => PedirDemandaAsync(umCliente, maquina.Id, inicio, inicio.AddHours(1)),
                () => PedirDemandaAsync(outroCliente, maquina.Id, inicio.AddMinutes(30), inicio.AddMinutes(90)));

            Assert.Equal(
                [HttpStatusCode.Created, HttpStatusCode.Conflict],
                respostas.Select(r => r.StatusCode).OrderBy(c => (int)c).ToList());
            var perdedor = respostas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            using var corpo = await Json.DocumentoAsync(perdedor);
            Assert.Equal(
                "Uma demanda do parque não desmarca outra.", corpo.RootElement.GetProperty("detail").GetString());
            Assert.Equal((rodada + 1), await DemandasAtivasAsync(maquina.Id));
            var estado = await EstadoAsync(pessoal.Id);
            Assert.Equal(("CanceladaPelaEquipe", Motivo), estado);
        }
    }

    [Fact]
    public async Task Desmarcacao_e_cancelamento_pelo_dono_ao_mesmo_tempo_cancelam_a_pessoal_uma_vez_so_e_a_demanda_sai()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var equipe = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);

        for (var rodada = 0; rodada < Rodadas; rodada++)
        {
            var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
            var inicio = InicioDaRodada(rodada);
            using var doDono = fabrica.Contas.ClienteCom(dono.Token);
            using var outroDoDono = fabrica.Contas.ClienteCom(dono.Token);
            using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
            var pessoal = await ReservarOkAsync(doDono, maquina.Id, inicio, inicio.AddHours(1));

            var respostas = await EmParaleloAsync(
                () => PedirDemandaAsync(daEquipe, maquina.Id, inicio, inicio.AddHours(1)),
                () => CancelarAsync(outroDoDono, pessoal.Id));

            // A vaga sai de qualquer jeito: a demanda é sempre criada. O dono recebe 200 ou 409, nunca 500.
            Assert.Equal(HttpStatusCode.Created, respostas[0].StatusCode);
            var doCancelamento = respostas[1].StatusCode;
            Assert.True(
                doCancelamento is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"Rodada {rodada}: o dono recebeu {doCancelamento}.");

            // Uma vez só, e de acordo com quem chegou primeiro: o 200 do dono é o cancelamento dele; o 409, o da Equipe.
            var estado = await EstadoAsync(pessoal.Id);
            Assert.Equal(
                doCancelamento == HttpStatusCode.OK ? ("CanceladaPeloDono", (string?)null) : ("CanceladaPelaEquipe", Motivo),
                estado);
            Assert.Equal(rodada + 1, await DemandasAtivasAsync(maquina.Id));
        }
    }

    [Fact]
    public async Task Desmarcacao_e_revogacao_do_treinamento_do_dono_ao_mesmo_tempo_cancelam_uma_vez_so_sem_deadlock()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var equipe = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);

        for (var rodada = 0; rodada < Rodadas; rodada++)
        {
            var dono = await fabrica.Contas.CriarAsync(Papel.Usuario);
            var treinamento = await _reservas.TreinarAsync(dono.Id, tipo.Id);
            var inicio = InicioDaRodada(rodada);
            using var doDono = fabrica.Contas.ClienteCom(dono.Token);
            using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
            using var quemRevoga = await _reservas.ClienteDaEquipeAsync();
            var pessoal = await ReservarOkAsync(doDono, maquina.Id, inicio, inicio.AddHours(1));

            var respostas = await EmParaleloAsync(
                () => PedirDemandaAsync(daEquipe, maquina.Id, inicio, inicio.AddHours(1)),
                () => quemRevoga.PostAsJsonAsync(
                    $"/treinamentos/{treinamento.Id}/revogacao",
                    new { motivo = "Corrida." },
                    TestContext.Current.CancellationToken));

            Assert.True(
                respostas[0].StatusCode == HttpStatusCode.Created,
                $"Rodada {rodada}: a demanda respondeu {respostas[0].StatusCode}: {await respostas[0].Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
            Assert.Equal(HttpStatusCode.OK, respostas[1].StatusCode);
            // Cancelada uma vez só, com o motivo de quem chegou primeiro.
            var (situacao, motivo) = await EstadoAsync(pessoal.Id);
            Assert.Equal("CanceladaPelaEquipe", situacao);
            Assert.Contains(motivo, new[] { Motivo, "Treinamento revogado" });
            Assert.Equal(rodada + 1, await DemandasAtivasAsync(maquina.Id));
        }
    }
}
