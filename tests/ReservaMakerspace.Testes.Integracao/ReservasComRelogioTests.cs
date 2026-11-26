using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// As bordas de tempo das reservas com o relógio avançando de verdade (<see cref="FakeTimeProvider"/>): começar agora,
/// a duração máxima, os 7 e os 30 dias, a validade do treinamento, o limite de reservas pessoais, a revogação e o
/// cancelamento depois do início. Cada teste sobe a API num banco próprio e junta vários passos, porque isso custa
/// mais do que um teste do banco compartilhado.
/// </summary>
public class ReservasComRelogioTests(BancosDeTeste bancos) : IClassFixture<BancosDeTeste>
{
    private static readonly DateTimeOffset T0 = new(2027, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private async Task<FabricaComRelogio> NovaFabricaAsync() =>
        new(await bancos.NovoBancoAsync(), new FakeTimeProvider(T0));

    private static async Task ConferirRecusaAsync(HttpResponseMessage resposta, HttpStatusCode status, string detalhe)
    {
        Assert.Equal(status, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal(detalhe, corpo.RootElement.GetProperty("detail").GetString());
    }

    private static async Task<HttpStatusCode> ReservarComoAsync(
        AuxiliarDeReservas auxiliar,
        ContaDeTeste pessoa,
        Guid maquinaId,
        DateTimeOffset inicio,
        DateTimeOffset fim,
        string finalidade = "ProjetoPessoal")
    {
        using var cliente = await auxiliar.ClienteAsync(pessoa);
        return (await ReservarAsync(cliente, maquinaId, inicio, fim, finalidade)).StatusCode;
    }

    [Fact]
    public async Task Bordas_de_inicio_duracao_e_antecedencia_do_projeto_pessoal_e_da_demanda()
    {
        await using var fabrica = await NovaFabricaAsync();
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        var tipo = await auxiliar.CriarTipoAsync(minutosMaximos: 120);
        // Cada caso numa pessoa e numa máquina novas: o limite de 2 pessoais e a agenda não se misturam.
        async Task<(ContaDeTeste Pessoa, EquipamentoResposta Maquina)> NovoAsync(Papel papel = Papel.Usuario) =>
            (await auxiliar.PessoaTreinadaAsync(tipo.Id, papel), await auxiliar.CriarMaquinaAsync(tipo.Id));

        // Começar agora é aceito; um segundo antes, não.
        var (p1, m1) = await NovoAsync();
        Assert.Equal(HttpStatusCode.Created, await ReservarComoAsync(auxiliar, p1, m1.Id, T0, T0.AddHours(1)));
        var (p2, m2) = await NovoAsync();
        using (var cliente = await auxiliar.ClienteAsync(p2))
        {
            await ConferirRecusaAsync(
                await ReservarAsync(cliente, m2.Id, T0.AddSeconds(-1), T0.AddHours(1)),
                HttpStatusCode.UnprocessableEntity, "O início da reserva não pode ser no passado.");
        }

        // Durar exatamente a máxima do tipo é aceito; 1 minuto a mais, não.
        var (p3, m3) = await NovoAsync();
        Assert.Equal(HttpStatusCode.Created, await ReservarComoAsync(auxiliar, p3, m3.Id, T0.AddHours(1), T0.AddHours(3)));
        var (p4, m4) = await NovoAsync();
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            await ReservarComoAsync(auxiliar, p4, m4.Id, T0.AddHours(1), T0.AddHours(3).AddMinutes(1)));

        // Começar em exatamente 7 dias é aceito; 1 segundo depois, não.
        var (p5, m5) = await NovoAsync();
        Assert.Equal(
            HttpStatusCode.Created,
            await ReservarComoAsync(auxiliar, p5, m5.Id, T0.AddDays(7), T0.AddDays(7).AddHours(1)));
        var (p6, m6) = await NovoAsync();
        using (var cliente = await auxiliar.ClienteAsync(p6))
        {
            await ConferirRecusaAsync(
                await ReservarAsync(cliente, m6.Id, T0.AddDays(7).AddSeconds(1), T0.AddDays(7).AddHours(1)),
                HttpStatusCode.UnprocessableEntity, "Uma reserva pessoal só pode começar em até 7 dias.");
        }

        // A demanda do parque: 30 dias exatos são aceitos; 1 segundo depois, não. E sem duração máxima.
        var (equipe, m7) = await NovoAsync(Papel.Equipe);
        Assert.Equal(
            HttpStatusCode.Created,
            await ReservarComoAsync(auxiliar, equipe, m7.Id, T0.AddDays(30), T0.AddDays(30).AddHours(10), "DemandaDoParque"));
        var m8 = await auxiliar.CriarMaquinaAsync(tipo.Id);
        using (var cliente = await auxiliar.ClienteAsync(equipe))
        {
            await ConferirRecusaAsync(
                await ReservarAsync(cliente, m8.Id, T0.AddDays(30).AddSeconds(1), T0.AddDays(30).AddHours(1), "DemandaDoParque"),
                HttpStatusCode.UnprocessableEntity, "Uma demanda do parque só pode começar em até 30 dias.");
        }
    }

    [Fact]
    public async Task O_treinamento_precisa_valer_ate_o_fim_da_reserva_e_nao_so_no_dia_em_que_se_reserva()
    {
        await using var fabrica = await NovaFabricaAsync();
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        var tipo = await auxiliar.CriarTipoAsync(minutosMaximos: 120);
        var m1 = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var m2 = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var m3 = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var treinamento = await auxiliar.TreinarAsync(pessoa.Id, tipo.Id);
        var validoAte = treinamento.ValidoAte;
        Assert.Equal(T0.AddMonths(12), validoAte);

        // Meia hora antes de vencer: acabar exatamente no ValidoAte é aceito, 1 segundo depois, não.
        fabrica.Relogio.SetUtcNow(validoAte.AddMinutes(-30));
        using (var cliente = await auxiliar.ClienteAsync(pessoa))
        {
            await ConferirRecusaAsync(
                await ReservarAsync(cliente, m1.Id, validoAte.AddMinutes(-30), validoAte.AddSeconds(1)),
                HttpStatusCode.UnprocessableEntity, $"Sem treinamento válido em {tipo.Nome} até o fim da reserva.");
            var aceita = await ReservarOkAsync(cliente, m2.Id, validoAte.AddMinutes(-30), validoAte);
            Assert.Equal(validoAte, aceita.Fim);
        }

        // Vencido: no instante exato do ValidoAte, nenhuma reserva nova passa, mesmo curta.
        fabrica.Relogio.SetUtcNow(validoAte);
        using (var cliente = await auxiliar.ClienteAsync(pessoa))
        {
            await ConferirRecusaAsync(
                await ReservarAsync(cliente, m3.Id, validoAte, validoAte.AddMinutes(10)),
                HttpStatusCode.UnprocessableEntity, $"Sem treinamento válido em {tipo.Nome} até o fim da reserva.");
        }

        // Renovar é registrar de novo: o novo treinamento vale e a reserva passa.
        await auxiliar.TreinarAsync(pessoa.Id, tipo.Id);
        using (var cliente = await auxiliar.ClienteAsync(pessoa))
        {
            await ReservarOkAsync(cliente, m3.Id, validoAte, validoAte.AddMinutes(10));
        }
    }

    [Fact]
    public async Task A_reserva_pessoal_conta_no_limite_ate_o_instante_em_que_acaba()
    {
        await using var fabrica = await NovaFabricaAsync();
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        var tipo = await auxiliar.CriarTipoAsync(minutosMaximos: 120);
        var maquinas = new[]
        {
            await auxiliar.CriarMaquinaAsync(tipo.Id),
            await auxiliar.CriarMaquinaAsync(tipo.Id),
            await auxiliar.CriarMaquinaAsync(tipo.Id),
        };
        var pessoa = await auxiliar.PessoaTreinadaAsync(tipo.Id);
        var fimDasDuas = T0.AddHours(2);
        Assert.Equal(HttpStatusCode.Created, await ReservarComoAsync(auxiliar, pessoa, maquinas[0].Id, T0.AddHours(1), fimDasDuas));
        Assert.Equal(HttpStatusCode.Created, await ReservarComoAsync(auxiliar, pessoa, maquinas[1].Id, T0.AddHours(1), fimDasDuas));
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            await ReservarComoAsync(auxiliar, pessoa, maquinas[2].Id, T0.AddHours(3), T0.AddHours(4)));

        // Com as duas em andamento e a 1 segundo de acabar, ainda contam (fim > agora).
        fabrica.Relogio.SetUtcNow(fimDasDuas.AddSeconds(-1));
        using (var cliente = await auxiliar.ClienteAsync(pessoa))
        {
            await ConferirRecusaAsync(
                await ReservarAsync(cliente, maquinas[2].Id, fimDasDuas.AddHours(1), fimDasDuas.AddHours(2)),
                HttpStatusCode.UnprocessableEntity,
                "Você já tem 2 reservas pessoais ativas. Cancele uma ou espere uma acabar.");
        }

        // No instante em que acabam, deixam de contar.
        fabrica.Relogio.SetUtcNow(fimDasDuas);
        Assert.Equal(
            HttpStatusCode.Created,
            await ReservarComoAsync(auxiliar, pessoa, maquinas[2].Id, fimDasDuas.AddHours(1), fimDasDuas.AddHours(2)));
    }

    [Fact]
    public async Task Revogar_cancela_as_futuras_da_pessoa_no_tipo_e_deixa_a_em_andamento_as_de_outro_tipo_e_as_de_outra_pessoa()
    {
        await using var fabrica = await NovaFabricaAsync();
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        var tipo = await auxiliar.CriarTipoAsync(minutosMaximos: 600);
        var outroTipo = await auxiliar.CriarTipoAsync(minutosMaximos: 600);
        var a = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var b = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var c = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var d = await auxiliar.CriarMaquinaAsync(outroTipo.Id);
        var alvo = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var treinamento = await auxiliar.TreinarAsync(alvo.Id, tipo.Id);
        await auxiliar.TreinarAsync(alvo.Id, outroTipo.Id);
        var outra = await auxiliar.PessoaTreinadaAsync(tipo.Id);
        var equipe = await auxiliar.EquipeAsync();

        ReservaResposta emAndamento, pessoalFutura, demandaFutura, doOutroTipo, daOutraPessoa;
        using (var cliente = await auxiliar.ClienteAsync(alvo))
        {
            emAndamento = await ReservarOkAsync(cliente, a.Id, T0, T0.AddHours(1), "DemandaDoParque");
            pessoalFutura = await ReservarOkAsync(cliente, b.Id, T0.AddHours(3), T0.AddHours(4));
            demandaFutura = await ReservarOkAsync(cliente, c.Id, T0.AddHours(5), T0.AddHours(6), "DemandaDoParque");
            doOutroTipo = await ReservarOkAsync(cliente, d.Id, T0.AddHours(3), T0.AddHours(4));
        }

        using (var cliente = await auxiliar.ClienteAsync(outra))
        {
            daOutraPessoa = await ReservarOkAsync(cliente, a.Id, T0.AddHours(2), T0.AddHours(3));
        }

        // Meia hora depois: a primeira está em andamento.
        fabrica.Relogio.Advance(TimeSpan.FromMinutes(30));
        var momento = fabrica.Relogio.GetUtcNow();
        Assert.Equal(HttpStatusCode.OK, (await auxiliar.RevogarAsync(treinamento.Id)).StatusCode);

        using var daEquipe = await auxiliar.ClienteDaEquipeAsync();
        var reservas = (await ListarAsync(daEquipe, $"/reservas?usuarioId={alvo.Id}")).ToDictionary(r => r.Id);
        Assert.Equal(SituacaoDaReserva.Ativa, reservas[emAndamento.Id].Situacao);
        Assert.Equal(SituacaoDaReserva.Ativa, reservas[doOutroTipo.Id].Situacao);
        foreach (var cancelada in new[] { pessoalFutura, demandaFutura })
        {
            var atual = reservas[cancelada.Id];
            Assert.Equal(SituacaoDaReserva.CanceladaPelaEquipe, atual.Situacao);
            Assert.Equal("Treinamento revogado", atual.MotivoDoCancelamento);
            Assert.Equal(momento, atual.CanceladaEm);
            Assert.Equal("Pessoa de Teste", atual.CanceladaPorNome);
        }

        Assert.Equal(
            SituacaoDaReserva.Ativa,
            Assert.Single(await ListarAsync(daEquipe, $"/reservas?usuarioId={outra.Id}"), r => r.Id == daOutraPessoa.Id).Situacao);
        Assert.NotEqual(Guid.Empty, equipe.Id);

        // A pessoa sem treinamento não reserva mais nesse tipo, mas continua reservando no outro.
        var e = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var f = await auxiliar.CriarMaquinaAsync(outroTipo.Id);
        using (var cliente = await auxiliar.ClienteAsync(alvo))
        {
            Assert.Equal(
                HttpStatusCode.UnprocessableEntity,
                (await ReservarAsync(cliente, e.Id, T0.AddDays(1), T0.AddDays(1).AddHours(1), "DemandaDoParque")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Created,
                (await ReservarAsync(cliente, f.Id, T0.AddDays(1), T0.AddDays(1).AddHours(1), "DemandaDoParque")).StatusCode);
        }

        // Máquina só com reserva cancelada pode ser desativada; a com reserva em andamento (e a futura de outra pessoa), não.
        using var coordenacao = await auxiliar.ClienteDaCoordenacaoAsync();
        Assert.Equal(HttpStatusCode.OK, (await AlterarAsync(coordenacao, b, ativo: false)).StatusCode);
        var recusada = await AlterarAsync(coordenacao, a, ativo: false);
        Assert.Equal(HttpStatusCode.Conflict, recusada.StatusCode);
        using (var corpo = await Json.DocumentoAsync(recusada))
        {
            Assert.Equal(2, corpo.RootElement.GetProperty("conflitos").GetArrayLength());
        }

        // Depois que a em andamento acaba (1 h) e a da outra pessoa também (3 h, no instante exato), não há mais nada ativo e futuro.
        fabrica.Relogio.SetUtcNow(T0.AddHours(3));
        using var coordenacaoDepois = await auxiliar.ClienteDaCoordenacaoAsync();
        Assert.Equal(HttpStatusCode.OK, (await AlterarAsync(coordenacaoDepois, a, ativo: false)).StatusCode);
    }

    private static Task<HttpResponseMessage> AlterarAsync(HttpClient cliente, EquipamentoResposta m, bool ativo) =>
        cliente.PutAsJsonAsync(
            $"/equipamentos/{m.Id}",
            new { nome = m.Nome, tipoId = m.TipoId, ativo },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task O_dono_cancela_ate_o_ultimo_segundo_antes_do_inicio_e_depois_disso_recebe_409()
    {
        await using var fabrica = await NovaFabricaAsync();
        var auxiliar = new AuxiliarDeReservas(fabrica.Contas);
        var tipo = await auxiliar.CriarTipoAsync(minutosMaximos: 120);
        var m1 = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var m2 = await auxiliar.CriarMaquinaAsync(tipo.Id);
        var pessoa = await auxiliar.PessoaTreinadaAsync(tipo.Id);
        ReservaResposta r1, r2;
        using (var cliente = await auxiliar.ClienteAsync(pessoa))
        {
            r1 = await ReservarOkAsync(cliente, m1.Id, T0.AddHours(1), T0.AddHours(2));
            r2 = await ReservarOkAsync(cliente, m2.Id, T0.AddHours(3), T0.AddHours(4));
        }

        // 1 segundo antes do início: ainda cancela.
        fabrica.Relogio.SetUtcNow(r1.Inicio.AddSeconds(-1));
        using (var cliente = await auxiliar.ClienteAsync(pessoa))
        {
            var resposta = await CancelarAsync(cliente, r1.Id);
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            var cancelada = await Json.LerAsync<ReservaResposta>(resposta);
            Assert.Equal(SituacaoDaReserva.CanceladaPeloDono, cancelada.Situacao);
            Assert.Equal(fabrica.Relogio.GetUtcNow(), cancelada.CanceladaEm);
        }

        // No instante do início, já começou.
        fabrica.Relogio.SetUtcNow(r2.Inicio);
        using (var cliente = await auxiliar.ClienteAsync(pessoa))
        {
            await ConferirRecusaAsync(await CancelarAsync(cliente, r2.Id), HttpStatusCode.Conflict, "A reserva já começou.");

            // Em andamento e depois de acabar: a mesma resposta, e a reserva continua ativa.
            fabrica.Relogio.Advance(TimeSpan.FromMinutes(30));
            await ConferirRecusaAsync(await CancelarAsync(cliente, r2.Id), HttpStatusCode.Conflict, "A reserva já começou.");
            fabrica.Relogio.SetUtcNow(r2.Fim.AddHours(1));
        }

        using (var cliente = await auxiliar.ClienteAsync(pessoa))
        {
            await ConferirRecusaAsync(await CancelarAsync(cliente, r2.Id), HttpStatusCode.Conflict, "A reserva já começou.");
            var lida = await Json.LerAsync<ReservaResposta>(
                await cliente.GetAsync($"/reservas/{r2.Id}", TestContext.Current.CancellationToken));
            Assert.Equal(SituacaoDaReserva.Ativa, lida.Situacao);
        }
    }
}
