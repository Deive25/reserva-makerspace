using System.Net;
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
}
