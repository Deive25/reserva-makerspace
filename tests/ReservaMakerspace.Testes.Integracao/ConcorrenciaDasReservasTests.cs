using System.Net;
using System.Net.Http.Json;
using Npgsql;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// Pedidos simultâneos (plano do 1.5, decisões 5 a 7). Cada teste repete várias rodadas, com um cliente por pedido e
/// <see cref="Task.WhenAll"/>, e nenhuma rodada pode terminar em 500. Cada rodada usa máquinas novas, e cada teste cria
/// as próprias pessoas e o próprio tipo, porque a coleção compartilha o banco.
/// </summary>
[Collection(ApiCollection.Nome)]
public class ConcorrenciaDasReservasTests(FabricaDaApi fabrica)
{
    private const int Rodadas = 12;

    private readonly AuxiliarDeReservas _reservas = new(fabrica.Contas);

    private static DateTimeOffset Amanha => Daqui(TimeSpan.FromDays(1));

    private async Task<HttpResponseMessage[]> EmParaleloAsync(params Func<Task<HttpResponseMessage>>[] pedidos) =>
        await Task.WhenAll(pedidos.Select(p => Task.Run(p)));

    private static List<HttpStatusCode> Codigos(IEnumerable<HttpResponseMessage> respostas) =>
        respostas.Select(r => r.StatusCode).OrderBy(c => (int)c).ToList();

    [Fact]
    public async Task Dois_pedidos_de_pessoas_diferentes_na_mesma_maquina_e_no_mesmo_horario_dao_um_201_e_um_409()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var primeira = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        var segunda = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);

        for (var rodada = 0; rodada < Rodadas; rodada++)
        {
            var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
            var inicio = Amanha.AddHours(rodada);
            using var umCliente = fabrica.Contas.ClienteCom(primeira.Token);
            using var outroCliente = fabrica.Contas.ClienteCom(segunda.Token);

            var respostas = await EmParaleloAsync(
                () => ReservarAsync(umCliente, maquina.Id, inicio, inicio.AddHours(2), "DemandaDoParque"),
                () => ReservarAsync(outroCliente, maquina.Id, inicio.AddHours(1), inicio.AddHours(3), "DemandaDoParque"));

            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], Codigos(respostas));
            // O perdedor recebe a lista de conflitos (a checagem em C# sob a trava da máquina, e não a restrição).
            var perdedor = respostas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            using var corpo = await Json.DocumentoAsync(perdedor);
            Assert.Single(corpo.RootElement.GetProperty("conflitos").EnumerateArray());
        }
    }

    [Fact]
    public async Task Varios_pedidos_simultaneos_na_mesma_maquina_deixam_exatamente_uma_reserva_ativa()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var pessoas = new List<ContaDeTeste>();
        for (var i = 0; i < 5; i++)
        {
            pessoas.Add(await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe));
        }

        for (var rodada = 0; rodada < 6; rodada++)
        {
            var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
            var inicio = Amanha.AddHours(rodada);
            var clientes = pessoas.Select(p => fabrica.Contas.ClienteCom(p.Token)).ToList();
            try
            {
                var respostas = await EmParaleloAsync(
                    clientes.Select(c => (Func<Task<HttpResponseMessage>>)(() =>
                        ReservarAsync(c, maquina.Id, inicio, inicio.AddHours(1), "DemandaDoParque"))).ToArray());

                Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
                Assert.Equal(4, respostas.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            }
            finally
            {
                clientes.ForEach(c => c.Dispose());
            }
        }
    }

    [Fact]
    public async Task Dois_pedidos_de_projeto_pessoal_de_pessoas_diferentes_na_mesma_maquina_dao_um_201_e_um_409()
    {
        var tipo = await _reservas.CriarTipoAsync();
        for (var rodada = 0; rodada < 4; rodada++)
        {
            var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
            var primeira = await _reservas.PessoaTreinadaAsync(tipo.Id);
            var segunda = await _reservas.PessoaTreinadaAsync(tipo.Id);
            var inicio = Amanha;
            using var umCliente = fabrica.Contas.ClienteCom(primeira.Token);
            using var outroCliente = fabrica.Contas.ClienteCom(segunda.Token);

            var respostas = await EmParaleloAsync(
                () => ReservarAsync(umCliente, maquina.Id, inicio, inicio.AddHours(1)),
                () => ReservarAsync(outroCliente, maquina.Id, inicio, inicio.AddHours(1)));

            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], Codigos(respostas));
        }
    }

    [Fact]
    public async Task Tres_reservas_pessoais_simultaneas_da_mesma_pessoa_em_maquinas_diferentes_dao_duas_201_e_uma_422()
    {
        var tipo = await _reservas.CriarTipoAsync();
        for (var rodada = 0; rodada < 8; rodada++)
        {
            var pessoa = await _reservas.PessoaTreinadaAsync(tipo.Id);
            var maquinas = new[]
            {
                await _reservas.CriarMaquinaAsync(tipo.Id),
                await _reservas.CriarMaquinaAsync(tipo.Id),
                await _reservas.CriarMaquinaAsync(tipo.Id),
            };
            var inicio = Amanha;
            var clientes = maquinas.Select(_ => fabrica.Contas.ClienteCom(pessoa.Token)).ToList();
            try
            {
                var respostas = await EmParaleloAsync(
                    maquinas.Select((m, i) => (Func<Task<HttpResponseMessage>>)(() =>
                        ReservarAsync(clientes[i], m.Id, inicio, inicio.AddHours(1)))).ToArray());

                Assert.Equal(
                    [HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity],
                    Codigos(respostas));
            }
            finally
            {
                clientes.ForEach(c => c.Dispose());
            }

            using var cliente = fabrica.Contas.ClienteCom(pessoa.Token);
            Assert.Equal(2, (await ListarAsync(cliente, "/me/reservas")).Count);
        }
    }

    [Fact]
    public async Task Dois_pedidos_da_mesma_pessoa_na_mesma_maquina_e_horario_dao_um_201_e_um_409()
    {
        var tipo = await _reservas.CriarTipoAsync();
        for (var rodada = 0; rodada < 6; rodada++)
        {
            var pessoa = await _reservas.PessoaTreinadaAsync(tipo.Id);
            var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
            var inicio = Amanha;
            using var umCliente = fabrica.Contas.ClienteCom(pessoa.Token);
            using var outroCliente = fabrica.Contas.ClienteCom(pessoa.Token);

            var respostas = await EmParaleloAsync(
                () => ReservarAsync(umCliente, maquina.Id, inicio, inicio.AddHours(1)),
                () => ReservarAsync(outroCliente, maquina.Id, inicio, inicio.AddHours(1)));

            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], Codigos(respostas));
        }
    }

    /// <summary>Reservas ativas e futuras da pessoa em máquinas do tipo. Com o treinamento revogado, devem ser zero.</summary>
    private async Task<long> ContarAtivasFuturasNoTipoAsync(Guid pessoaId, Guid tipoId)
    {
        await using var fonte = NpgsqlDataSource.Create(fabrica.ConnectionString);
        await using var comando = fonte.CreateCommand(
            """
            SELECT count(*) FROM reservas r JOIN equipamentos e ON e.id = r.equipamento_id
            WHERE r.operador_id = $1 AND e.tipo_id = $2 AND r.situacao = 'Ativa' AND r.fim > now()
            """);
        comando.Parameters.AddWithValue(pessoaId);
        comando.Parameters.AddWithValue(tipoId);
        return (long)(await comando.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // Item 5 do Foco: reserva e revogação simultâneas. Ou a reserva saiu 422 (a revogação chegou primeiro), ou saiu 201
    // e foi cancelada pela revogação. Nunca sobra reserva ativa e futura de quem perdeu o treinamento.
    [Theory]
    [InlineData(Papel.Usuario, FinalidadeDaReservaDoTeste.Pessoal)]
    [InlineData(Papel.Equipe, FinalidadeDaReservaDoTeste.Demanda)]
    public async Task Reservar_e_revogar_o_treinamento_ao_mesmo_tempo_nunca_deixa_reserva_ativa_sem_treinamento(
        Papel papel, FinalidadeDaReservaDoTeste finalidade)
    {
        var pessoa = await fabrica.Contas.CriarAsync(papel);
        var nome = finalidade == FinalidadeDaReservaDoTeste.Pessoal ? "ProjetoPessoal" : "DemandaDoParque";

        for (var rodada = 0; rodada < 15; rodada++)
        {
            var tipo = await _reservas.CriarTipoAsync();
            var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
            var treinamento = await _reservas.TreinarAsync(pessoa.Id, tipo.Id);
            var inicio = Amanha;
            using var daPessoa = fabrica.Contas.ClienteCom(pessoa.Token);
            using var daEquipe = await _reservas.ClienteDaEquipeAsync();

            var respostas = await EmParaleloAsync(
                () => ReservarAsync(daPessoa, maquina.Id, inicio, inicio.AddHours(1), nome),
                () => daEquipe.PostAsJsonAsync(
                    $"/treinamentos/{treinamento.Id}/revogacao", new { motivo = "Corrida." }, TestContext.Current.CancellationToken));

            var reserva = respostas[0].StatusCode;
            Assert.Equal(HttpStatusCode.OK, respostas[1].StatusCode);
            Assert.True(
                reserva is HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity,
                $"Rodada {rodada}: a reserva respondeu {reserva}.");
            Assert.Equal(0, await ContarAtivasFuturasNoTipoAsync(pessoa.Id, tipo.Id));

            if (reserva == HttpStatusCode.Created)
            {
                var criada = await Json.LerAsync<ReservaMakerspace.Contratos.Reservas.ReservaResposta>(respostas[0]);
                var lida = await Json.LerAsync<ReservaMakerspace.Contratos.Reservas.ReservaResposta>(
                    await daPessoa.GetAsync($"/reservas/{criada.Id}", TestContext.Current.CancellationToken));
                Assert.Equal(SituacaoDaReserva.CanceladaPelaEquipe, lida.Situacao);
                Assert.Equal("Treinamento revogado", lida.MotivoDoCancelamento);
            }
        }
    }

    [Fact]
    public async Task Varias_reservas_da_pessoa_contra_a_revogacao_nao_deixam_nenhuma_ativa_e_futura_no_tipo()
    {
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Equipe);
        for (var rodada = 0; rodada < 8; rodada++)
        {
            var tipo = await _reservas.CriarTipoAsync();
            var maquinas = new[] { await _reservas.CriarMaquinaAsync(tipo.Id), await _reservas.CriarMaquinaAsync(tipo.Id), await _reservas.CriarMaquinaAsync(tipo.Id) };
            var treinamento = await _reservas.TreinarAsync(pessoa.Id, tipo.Id);
            var inicio = Amanha;
            var clientes = maquinas.Select(_ => fabrica.Contas.ClienteCom(pessoa.Token)).ToList();
            using var daEquipe = await _reservas.ClienteDaEquipeAsync();
            try
            {
                var pedidos = maquinas
                    .Select((m, i) => (Func<Task<HttpResponseMessage>>)(() =>
                        ReservarAsync(clientes[i], m.Id, inicio, inicio.AddHours(1), "DemandaDoParque")))
                    .Append(() => daEquipe.PostAsJsonAsync(
                        $"/treinamentos/{treinamento.Id}/revogacao", new { motivo = "Corrida." }, TestContext.Current.CancellationToken))
                    .ToArray();

                var respostas = await EmParaleloAsync(pedidos);

                Assert.Equal(HttpStatusCode.OK, respostas[^1].StatusCode);
                Assert.All(
                    respostas[..^1],
                    r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity, $"Rodada {rodada}: {r.StatusCode}."));
                Assert.Equal(0, await ContarAtivasFuturasNoTipoAsync(pessoa.Id, tipo.Id));
            }
            finally
            {
                clientes.ForEach(c => c.Dispose());
            }
        }
    }

    [Fact]
    public async Task A_Equipe_revogando_o_proprio_treinamento_enquanto_reserva_nao_da_500_nem_deixa_reserva_ativa()
    {
        // Quem revoga e quem reserva é a mesma pessoa: a revogação grava a linha dela em treinamentos (revogado_por_id),
        // e a reserva trava a linha dela em usuarios. A variante NO KEY da trava evita o deadlock.
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        for (var rodada = 0; rodada < 15; rodada++)
        {
            var tipo = await _reservas.CriarTipoAsync();
            var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
            var treinamento = await _reservas.TreinarAsync(equipe.Id, tipo.Id);
            var inicio = Amanha;
            using var umCliente = fabrica.Contas.ClienteCom(equipe.Token);
            using var outroCliente = fabrica.Contas.ClienteCom(equipe.Token);

            var respostas = await EmParaleloAsync(
                () => ReservarAsync(umCliente, maquina.Id, inicio, inicio.AddHours(1)),
                () => outroCliente.PostAsJsonAsync(
                    $"/treinamentos/{treinamento.Id}/revogacao", new { motivo = "Corrida." }, TestContext.Current.CancellationToken));

            Assert.Equal(HttpStatusCode.OK, respostas[1].StatusCode);
            Assert.True(
                respostas[0].StatusCode is HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity,
                $"Rodada {rodada}: a reserva respondeu {respostas[0].StatusCode}.");
            Assert.Equal(0, await ContarAtivasFuturasNoTipoAsync(equipe.Id, tipo.Id));
        }
    }

    public enum FinalidadeDaReservaDoTeste
    {
        Pessoal,
        Demanda,
    }

    [Fact]
    public async Task Reservar_e_desativar_ou_trocar_o_tipo_da_maquina_ao_mesmo_tempo_nunca_deixa_reserva_futura_numa_maquina_inativa()
    {
        // Item 9 do Foco, lado da concorrência: o PUT trava a mesma linha da máquina que a criação da reserva.
        var tipo = await _reservas.CriarTipoAsync();
        var outroTipo = await _reservas.CriarTipoAsync();
        var pessoa = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        for (var rodada = 0; rodada < 12; rodada++)
        {
            var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
            var inicio = Amanha.AddHours(rodada);
            var desativar = rodada % 2 == 0;
            using var daPessoa = fabrica.Contas.ClienteCom(pessoa.Token);
            using var daCoordenacao = await _reservas.ClienteDaCoordenacaoAsync();

            var respostas = await EmParaleloAsync(
                () => ReservarAsync(daPessoa, maquina.Id, inicio, inicio.AddHours(1), "DemandaDoParque"),
                () => daCoordenacao.PutAsJsonAsync(
                    $"/equipamentos/{maquina.Id}",
                    new { nome = maquina.Nome, tipoId = desativar ? tipo.Id : outroTipo.Id, ativo = !desativar },
                    TestContext.Current.CancellationToken));

            var reserva = respostas[0].StatusCode;
            var alteracao = respostas[1].StatusCode;
            // Ou a reserva entrou e a alteração foi recusada (409), ou a alteração entrou e a reserva foi recusada
            // (422: máquina inativa, ou o treinamento do tipo antigo não vale no novo).
            Assert.True(
                (reserva == HttpStatusCode.Created && alteracao == HttpStatusCode.Conflict)
                || (reserva == HttpStatusCode.UnprocessableEntity && alteracao == HttpStatusCode.OK),
                $"Rodada {rodada}: reserva {reserva}, alteração {alteracao}.");

            Assert.Equal(0, await ContarReservasAtivasEmMaquinaForaDoTipoOuInativaAsync(maquina.Id, tipo.Id));
        }
    }

    private async Task<long> ContarReservasAtivasEmMaquinaForaDoTipoOuInativaAsync(Guid maquinaId, Guid tipoOriginalId)
    {
        await using var fonte = NpgsqlDataSource.Create(fabrica.ConnectionString);
        await using var comando = fonte.CreateCommand(
            """
            SELECT count(*) FROM reservas r JOIN equipamentos e ON e.id = r.equipamento_id
            WHERE e.id = $1 AND r.situacao = 'Ativa' AND (NOT e.ativo OR e.tipo_id <> $2)
            """);
        comando.Parameters.AddWithValue(maquinaId);
        comando.Parameters.AddWithValue(tipoOriginalId);
        return (long)(await comando.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
