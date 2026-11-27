using System.Net;
using System.Net.Http.Json;
using Npgsql;
using ReservaMakerspace.Contratos.Manutencoes;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// A manutenção com pedidos simultâneos (plano do 1.7, decisões 5, 6 e 11; item 10 do Foco). Cada teste repete várias
/// rodadas, com um cliente por pedido e <see cref="Task.WhenAll"/>, e nenhuma rodada pode terminar em 500. O tipo tem
/// uma máquina só (a trava 2 não interfere), cada rodada usa um horário diferente e um dono novo (o limite é de 2
/// pessoais ativas por pessoa). O relógio é o do sistema: as pessoais começam daqui a ~2 dias, longe das 24 h.
/// </summary>
[Collection(ApiCollection.Nome)]
public class ConcorrenciaDaManutencaoTests(FabricaDaApi fabrica)
{
    private const int Rodadas = 12;

    private const string Motivo = "Visita técnica do parque.";

    private const string MotivoDaManutencao = "Troca do bico.";

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

    /// <summary>Quantas reservas ativas da máquina cruzam uma manutenção ativa dela. Nunca pode ser mais que zero.</summary>
    private async Task<long> ReservasAtivasCruzandoManutencaoAsync(Guid maquinaId) =>
        await BancosDeTeste.ContarAsync(
            fabrica.ConnectionString,
            $"""
            SELECT count(*) FROM reservas r
            JOIN manutencoes m ON m.equipamento_id = r.equipamento_id
            WHERE r.equipamento_id = '{maquinaId}' AND r.situacao = 'Ativa' AND m.situacao = 'Ativa'
              AND r.inicio < m.fim AND m.inicio < r.fim
            """);

    private async Task<long> ManutencoesAtivasAsync(Guid maquinaId) =>
        await BancosDeTeste.ContarAsync(
            fabrica.ConnectionString,
            $"SELECT count(*) FROM manutencoes WHERE equipamento_id = '{maquinaId}' AND situacao = 'Ativa'");

    [Fact]
    public async Task Manutencao_e_reserva_no_mesmo_horario_dao_201_e_409_ou_201_e_a_reserva_cancelada_e_nunca_um_cruzamento()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var equipeTreinada = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);

        for (var rodada = 0; rodada < Rodadas; rodada++)
        {
            // As rodadas pares disputam com uma reserva pessoal, e as ímpares com uma demanda do parque.
            var ehDemanda = rodada % 2 == 1;
            var quemReserva = ehDemanda ? equipeTreinada : await _reservas.PessoaTreinadaAsync(tipo.Id);
            var inicio = InicioDaRodada(rodada);
            using var daManutencao = await _reservas.ClienteDaEquipeAsync();
            using var doReservante = fabrica.Contas.ClienteCom(quemReserva.Token);

            var respostas = await EmParaleloAsync(
                () => RegistrarManutencaoAsync(daManutencao, maquina.Id, inicio, inicio.AddHours(2), MotivoDaManutencao),
                () => ReservarAsync(
                    doReservante, maquina.Id, inicio.AddMinutes(30), inicio.AddMinutes(90),
                    ehDemanda ? "DemandaDoParque" : "ProjetoPessoal", ehDemanda ? "Prof. Teste" : null));

            // A manutenção sempre sai: se a reserva chegou antes, ela é cancelada; se chegou depois, é recusada.
            Assert.True(
                respostas[0].StatusCode == HttpStatusCode.Created,
                $"Rodada {rodada}: a manutenção respondeu {respostas[0].StatusCode}.");
            var daReserva = respostas[1].StatusCode;
            Assert.True(
                daReserva is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"Rodada {rodada}: a reserva respondeu {daReserva}.");
            if (daReserva == HttpStatusCode.Created)
            {
                var reserva = await Json.LerAsync<ReservaResposta>(respostas[1]);
                Assert.Equal(("CanceladaPorManutencao", MotivoDaManutencao), await EstadoAsync(reserva.Id));
            }

            Assert.Equal(0, await ReservasAtivasCruzandoManutencaoAsync(maquina.Id));
            Assert.Equal(rodada + 1, await ManutencoesAtivasAsync(maquina.Id));
        }
    }

    [Fact]
    public async Task Manutencao_e_desmarcacao_sobre_a_mesma_pessoal_cancelam_a_pessoal_uma_vez_so_e_nada_fica_cruzando()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var equipeTreinada = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);

        for (var rodada = 0; rodada < Rodadas; rodada++)
        {
            var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
            var inicio = InicioDaRodada(rodada);
            using var doDono = fabrica.Contas.ClienteCom(dono.Token);
            using var daManutencao = await _reservas.ClienteDaEquipeAsync();
            using var daDemanda = fabrica.Contas.ClienteCom(equipeTreinada.Token);
            var pessoal = await ReservarOkAsync(doDono, maquina.Id, inicio, inicio.AddHours(1));

            var respostas = await EmParaleloAsync(
                () => RegistrarManutencaoAsync(daManutencao, maquina.Id, inicio, inicio.AddHours(1), MotivoDaManutencao),
                () => ReservarAsync(
                    daDemanda, maquina.Id, inicio, inicio.AddHours(1), "DemandaDoParque", "Prof. Teste", motivoDesmarcacao: Motivo));

            Assert.True(
                respostas[0].StatusCode == HttpStatusCode.Created,
                $"Rodada {rodada}: a manutenção respondeu {respostas[0].StatusCode}.");
            var estadoDaPessoal = await EstadoAsync(pessoal.Id);
            switch (respostas[1].StatusCode)
            {
                case HttpStatusCode.Conflict:
                    // A manutenção já estava lá: "Uma manutenção não se desmarca.", e a pessoal é dela.
                    using (var corpo = await Json.DocumentoAsync(respostas[1]))
                    {
                        Assert.Equal(
                            "Uma manutenção não se desmarca.", corpo.RootElement.GetProperty("detail").GetString());
                    }

                    Assert.Equal(("CanceladaPorManutencao", MotivoDaManutencao), estadoDaPessoal);
                    break;
                case HttpStatusCode.Created:
                    // A demanda chegou antes: desmarcou a pessoal (o motivo dela fica) e a manutenção cancelou a demanda.
                    Assert.Equal(("CanceladaPelaEquipe", Motivo), estadoDaPessoal);
                    var demanda = await Json.LerAsync<ReservaResposta>(respostas[1]);
                    Assert.Equal(("CanceladaPorManutencao", MotivoDaManutencao), await EstadoAsync(demanda.Id));
                    break;
                default:
                    Assert.Fail($"Rodada {rodada}: a demanda respondeu {respostas[1].StatusCode}.");
                    break;
            }

            Assert.Equal(0, await ReservasAtivasCruzandoManutencaoAsync(maquina.Id));
        }
    }

    [Fact]
    public async Task Duas_manutencoes_sobrepostas_na_mesma_maquina_dao_um_201_e_um_409_e_uma_manutencao_ativa_so()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);

        for (var rodada = 0; rodada < Rodadas; rodada++)
        {
            var inicio = InicioDaRodada(rodada);
            using var umCliente = await _reservas.ClienteDaEquipeAsync();
            using var outroCliente = await _reservas.ClienteDaEquipeAsync();

            var respostas = await EmParaleloAsync(
                () => RegistrarManutencaoAsync(umCliente, maquina.Id, inicio, inicio.AddHours(2), "Primeira."),
                () => RegistrarManutencaoAsync(outroCliente, maquina.Id, inicio.AddHours(1), inicio.AddMinutes(150), "Segunda."));

            Assert.Equal(
                [HttpStatusCode.Created, HttpStatusCode.Conflict],
                respostas.Select(r => r.StatusCode).OrderBy(c => (int)c).ToList());
            var perdedor = respostas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            using var corpo = await Json.DocumentoAsync(perdedor);
            // A trava da máquina põe os dois em fila: o segundo vê o primeiro e recebe a lista (e não a violação do banco).
            Assert.Equal(1, corpo.RootElement.GetProperty("manutencoes").GetArrayLength());
            Assert.Equal(rodada + 1, await ManutencoesAtivasAsync(maquina.Id));
        }
    }

    [Fact]
    public async Task Manutencao_e_revogacao_do_treinamento_do_dono_cancelam_cada_pessoal_uma_vez_so_sem_deadlock()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);

        for (var rodada = 0; rodada < Rodadas; rodada++)
        {
            var dono = await fabrica.Contas.CriarAsync(Papel.Usuario);
            var treinamento = await _reservas.TreinarAsync(dono.Id, tipo.Id);
            var inicio = InicioDaRodada(rodada);
            using var doDono = fabrica.Contas.ClienteCom(dono.Token);
            using var daManutencao = await _reservas.ClienteDaEquipeAsync();
            using var quemRevoga = await _reservas.ClienteDaEquipeAsync();
            // Duas pessoais do dono dentro da manutenção: as duas linhas travadas em ordem de id, pelas duas rotas.
            var primeira = await ReservarOkAsync(doDono, maquina.Id, inicio, inicio.AddHours(1));
            var segunda = await ReservarOkAsync(doDono, maquina.Id, inicio.AddHours(1), inicio.AddHours(2));

            var respostas = await EmParaleloAsync(
                () => RegistrarManutencaoAsync(daManutencao, maquina.Id, inicio, inicio.AddHours(2), MotivoDaManutencao),
                () => quemRevoga.PostAsJsonAsync(
                    $"/treinamentos/{treinamento.Id}/revogacao",
                    new { motivo = "Corrida." },
                    TestContext.Current.CancellationToken));

            Assert.True(
                respostas[0].StatusCode == HttpStatusCode.Created,
                $"Rodada {rodada}: a manutenção respondeu {respostas[0].StatusCode}.");
            Assert.True(
                respostas[1].StatusCode == HttpStatusCode.OK,
                $"Rodada {rodada}: a revogação respondeu {respostas[1].StatusCode}.");
            // Cada uma foi cancelada uma vez só, com o motivo de quem chegou primeiro.
            foreach (var reserva in new[] { primeira, segunda })
            {
                var estado = await EstadoAsync(reserva.Id);
                Assert.True(
                    estado is ("CanceladaPorManutencao", MotivoDaManutencao) or ("CanceladaPelaEquipe", "Treinamento revogado"),
                    $"Rodada {rodada}: a reserva ficou {estado}.");
            }

            Assert.Equal(0, await ReservasAtivasCruzandoManutencaoAsync(maquina.Id));
        }
    }

    [Fact]
    public async Task Dois_encerramentos_da_mesma_manutencao_dao_um_200_e_um_409()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);

        for (var rodada = 0; rodada < Rodadas; rodada++)
        {
            var inicio = InicioDaRodada(rodada);
            using var registra = await _reservas.ClienteDaEquipeAsync();
            using var umCliente = await _reservas.ClienteDaEquipeAsync();
            using var outroCliente = await _reservas.ClienteDaEquipeAsync();
            var manutencao = await RegistrarManutencaoOkAsync(registra, maquina.Id, inicio, inicio.AddHours(2));

            var respostas = await EmParaleloAsync(
                () => EncerrarManutencaoAsync(umCliente, manutencao.Id),
                () => EncerrarManutencaoAsync(outroCliente, manutencao.Id));

            Assert.Equal(
                [HttpStatusCode.OK, HttpStatusCode.Conflict],
                respostas.Select(r => r.StatusCode).OrderBy(c => (int)c).ToList());
            var vencedor = await Json.LerAsync<ManutencaoResposta>(respostas.Single(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(SituacaoDaManutencao.Cancelada, vencedor.Situacao);
            using var corpo = await Json.DocumentoAsync(respostas.Single(r => r.StatusCode == HttpStatusCode.Conflict));
            Assert.Equal("A manutenção já foi cancelada.", corpo.RootElement.GetProperty("detail").GetString());
            // Nenhuma ficou ativa: encerrar uma que ainda não começou cancela.
            Assert.Equal(0, await ManutencoesAtivasAsync(maquina.Id));
        }
    }
}
