using System.Net;
using System.Text.Json;
using Npgsql;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// A desmarcação no <c>POST /reservas</c> (plano do 1.6, decisões 4 a 6; itens 5 a 9 e 12 do Foco), no banco
/// compartilhado e com o relógio do sistema: as pessoais começam daqui a ~2 dias, longe das 24 h (as bordas das 24 h
/// ficam em <see cref="DesmarcacaoComRelogioTests"/>). Cada cenário usa um tipo novo com uma máquina só, a não ser
/// que o teste precise de outra: com outra máquina ativa livre, a trava 2 recusaria.
/// </summary>
[Collection(ApiCollection.Nome)]
public class DesmarcacaoTests(FabricaDaApi fabrica)
{
    private const string Motivo = "Visita técnica do parque.";

    private const string Conflito = "A máquina já tem uma reserva ativa nesse horário.";

    private readonly AuxiliarDeReservas _reservas = new(fabrica.Contas);

    private sealed record Cenario(
        TipoDeEquipamentoResposta Tipo,
        EquipamentoResposta Maquina,
        ContaDeTeste Dono,
        ContaDeTeste Equipe,
        ReservaResposta Pessoal);

    private static DateTimeOffset Inicio => Dia(2).AddHours(10);

    private static DateTimeOffset Fim => Dia(2).AddHours(12);

    /// <summary>Um tipo com uma máquina, uma pessoa da Equipe treinada e uma pessoal do dono em [10h, 11h) de daqui a 2 dias.</summary>
    private async Task<Cenario> MontarAsync(Papel papelDaEquipe = Papel.Equipe)
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var equipe = await _reservas.PessoaTreinadaAsync(tipo.Id, papelDaEquipe);
        using var cliente = fabrica.Contas.ClienteCom(dono.Token);
        var pessoal = await ReservarOkAsync(cliente, maquina.Id, Inicio, Inicio.AddHours(1));
        return new Cenario(tipo, maquina, dono, equipe, pessoal);
    }

    private async Task<HttpResponseMessage> PedirDemandaAsync(
        ContaDeTeste equipe, Guid maquinaId, DateTimeOffset inicio, DateTimeOffset fim, string? motivo)
    {
        using var cliente = fabrica.Contas.ClienteCom(equipe.Token);
        return await ReservarAsync(cliente, maquinaId, inicio, fim, "DemandaDoParque", "Prof. Teste", motivoDesmarcacao: motivo);
    }

    private sealed record EstadoNoBanco(string Situacao, DateTimeOffset? CanceladaEm, Guid? CanceladaPorId, string? Motivo);

    private async Task<EstadoNoBanco> EstadoAsync(Guid reservaId)
    {
        await using var fonte = NpgsqlDataSource.Create(fabrica.ConnectionString);
        await using var comando = fonte.CreateCommand(
            "SELECT situacao, cancelada_em, cancelada_por_id, motivo_do_cancelamento FROM reservas WHERE id = $1");
        comando.Parameters.AddWithValue(reservaId);
        await using var leitor = await comando.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await leitor.ReadAsync(TestContext.Current.CancellationToken));
        return new EstadoNoBanco(
            leitor.GetString(0),
            await leitor.IsDBNullAsync(1) ? null : leitor.GetFieldValue<DateTimeOffset>(1),
            await leitor.IsDBNullAsync(2) ? null : leitor.GetGuid(2),
            await leitor.IsDBNullAsync(3) ? null : leitor.GetString(3));
    }

    private async Task AssertAtivaAsync(params Guid[] reservas)
    {
        foreach (var id in reservas)
        {
            Assert.Equal("Ativa", (await EstadoAsync(id)).Situacao);
        }
    }

    private static async Task<JsonDocument> LerConflitoAsync(HttpResponseMessage resposta)
    {
        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        return await Json.DocumentoAsync(resposta);
    }

    private static string Detalhe(JsonDocument corpo) => corpo.RootElement.GetProperty("detail").GetString()!;

    // Trava 4 (item 7).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sem_motivo_o_409_traz_os_conflitos_com_o_id_e_o_nome_e_nada_e_cancelado(string? motivo)
    {
        var c = await MontarAsync();

        using var corpo = await LerConflitoAsync(await PedirDemandaAsync(c.Equipe, c.Maquina.Id, Inicio, Fim, motivo));
        var detalhe = Detalhe(corpo);

        Assert.Equal(Conflito, detalhe);
        var conflito = Assert.Single(corpo.RootElement.GetProperty("conflitos").EnumerateArray());
        Assert.Equal(c.Pessoal.Id, conflito.GetProperty("reservaId").GetGuid());
        Assert.Equal("Pessoa de Teste", conflito.GetProperty("operadorNome").GetString());
        Assert.Equal(0, corpo.RootElement.GetProperty("maquinasLivres").GetArrayLength());
        await AssertAtivaAsync(c.Pessoal.Id);
    }

    // Trava 3 (item 6).
    [Fact]
    public async Task Com_uma_demanda_entre_os_conflitos_o_409_nao_cancela_nada_nem_as_pessoais_da_mesma_lista()
    {
        var c = await MontarAsync();
        using var daEquipe = fabrica.Contas.ClienteCom(c.Equipe.Token);
        var outraDemanda = await ReservarOkAsync(
            daEquipe, c.Maquina.Id, Inicio.AddHours(1), Inicio.AddHours(2), "DemandaDoParque", "Prof. Daniel");

        // A nova pega a pessoal [10h, 11h) e a demanda [11h, 12h).
        using var corpo = await LerConflitoAsync(await PedirDemandaAsync(c.Equipe, c.Maquina.Id, Inicio, Fim, Motivo));
        var detalhe = Detalhe(corpo);

        Assert.Equal("Uma demanda do parque não desmarca outra.", detalhe);
        Assert.Equal(2, corpo.RootElement.GetProperty("conflitos").GetArrayLength());
        await AssertAtivaAsync(c.Pessoal.Id, outraDemanda.Id);
    }

    // Trava 1 vem antes da trava 2: a pessoal que começa em 2 h não se desmarca, mesmo com outra máquina livre.
    [Fact]
    public async Task Pessoal_que_comeca_em_menos_de_24_h_nao_e_desmarcada_e_a_recusa_vem_antes_da_maquina_livre()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        await _reservas.CriarMaquinaAsync(tipo.Id);
        var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var equipe = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        var inicio = Daqui(TimeSpan.FromHours(2));
        using var doDono = fabrica.Contas.ClienteCom(dono.Token);
        var pessoal = await ReservarOkAsync(doDono, maquina.Id, inicio, inicio.AddHours(1));

        using var corpo = await LerConflitoAsync(
            await PedirDemandaAsync(equipe, maquina.Id, inicio, inicio.AddHours(1), Motivo));
        var detalhe = Detalhe(corpo);

        Assert.Equal("Só dá para desmarcar reservas que começam daqui a mais de 24 h.", detalhe);
        await AssertAtivaAsync(pessoal.Id);
    }

    // Trava 2 (item 5).
    [Fact]
    public async Task Com_outra_maquina_do_tipo_livre_o_409_sugere_a_maquina_e_nada_e_cancelado()
    {
        var c = await MontarAsync();
        var livre = await _reservas.CriarMaquinaAsync(c.Tipo.Id);

        using var corpo = await LerConflitoAsync(await PedirDemandaAsync(c.Equipe, c.Maquina.Id, Inicio, Fim, Motivo));
        var detalhe = Detalhe(corpo);

        Assert.Equal($"A {livre.Nome} está livre nesse horário. Reserve-a em vez de desmarcar.", detalhe);
        var sugeridas = corpo.RootElement.GetProperty("maquinasLivres").EnumerateArray().ToList();
        Assert.Equal(livre.Id, Assert.Single(sugeridas).GetProperty("id").GetGuid());
        Assert.Equal(c.Pessoal.Id, Assert.Single(corpo.RootElement.GetProperty("conflitos").EnumerateArray()).GetProperty("reservaId").GetGuid());
        await AssertAtivaAsync(c.Pessoal.Id);
    }

    public enum OutraMaquina
    {
        LivreSoEmParteDoIntervalo,
        Inativa,
        LivreMasDeOutroTipo,
    }

    [Theory]
    [InlineData(OutraMaquina.LivreSoEmParteDoIntervalo)]
    [InlineData(OutraMaquina.Inativa)]
    [InlineData(OutraMaquina.LivreMasDeOutroTipo)]
    public async Task Desmarca_quando_nenhuma_outra_maquina_do_tipo_esta_livre_no_intervalo_inteiro(OutraMaquina caso)
    {
        var c = await MontarAsync();
        switch (caso)
        {
            case OutraMaquina.LivreSoEmParteDoIntervalo:
                // Ocupada de 11h a 12h: não está livre em [10h, 12h), mesmo livre às 10h.
                var parcial = await _reservas.CriarMaquinaAsync(c.Tipo.Id);
                using (var daEquipe = fabrica.Contas.ClienteCom(c.Equipe.Token))
                {
                    await ReservarOkAsync(
                        daEquipe, parcial.Id, Inicio.AddHours(1), Inicio.AddHours(2), "DemandaDoParque", "Prof. Daniel");
                }

                break;
            case OutraMaquina.Inativa:
                await _reservas.CriarMaquinaAsync(c.Tipo.Id, ativo: false);
                break;
            case OutraMaquina.LivreMasDeOutroTipo:
                await _reservas.CriarMaquinaAsync((await _reservas.CriarTipoAsync()).Id);
                break;
        }

        var resposta = await PedirDemandaAsync(c.Equipe, c.Maquina.Id, Inicio, Fim, Motivo);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal("CanceladaPelaEquipe", (await EstadoAsync(c.Pessoal.Id)).Situacao);
    }

    // Desmarcação aceita (item 9); a Coordenação também desmarca.
    [Theory]
    [InlineData(Papel.Equipe)]
    [InlineData(Papel.Coordenacao)]
    public async Task Desmarcacao_aceita_cancela_as_pessoais_do_intervalo_e_cria_a_demanda(Papel papel)
    {
        var c = await MontarAsync(papel);
        var segundoDono = await _reservas.PessoaTreinadaAsync(c.Tipo.Id);
        var terceiroDono = await _reservas.PessoaTreinadaAsync(c.Tipo.Id);
        // O dono já tem a pessoal [10h, 11h); ganha mais uma, fora do intervalo pedido, encostada no fim: fica intacta.
        using var doDono = fabrica.Contas.ClienteCom(c.Dono.Token);
        using var doSegundo = fabrica.Contas.ClienteCom(segundoDono.Token);
        using var doTerceiro = fabrica.Contas.ClienteCom(terceiroDono.Token);
        var foraDoIntervalo = await ReservarOkAsync(doDono, c.Maquina.Id, Fim, Fim.AddHours(1));
        var segunda = await ReservarOkAsync(doSegundo, c.Maquina.Id, Inicio.AddHours(1), Inicio.AddHours(2));
        // Outra máquina do tipo, ocupada no intervalo (a trava 2 não recusa) e que não pode ser tocada.
        var outraMaquina = await _reservas.CriarMaquinaAsync(c.Tipo.Id);
        var deOutraMaquina = await ReservarOkAsync(doTerceiro, outraMaquina.Id, Inicio, Fim);
        var antes = TimeProvider.System.GetUtcNow();

        var resposta = await PedirDemandaAsync(c.Equipe, c.Maquina.Id, Inicio, Fim, $"  {Motivo}  ");

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var demanda = await Json.LerAsync<ReservaResposta>(resposta);
        Assert.Equal(SituacaoDaReserva.Ativa, demanda.Situacao);
        Assert.Equal(FinalidadeDaReserva.DemandaDoParque, demanda.Finalidade);
        Assert.Equal(c.Equipe.Id, demanda.OperadorId);
        foreach (var desmarcada in new[] { c.Pessoal.Id, segunda.Id })
        {
            var estado = await EstadoAsync(desmarcada);
            Assert.Equal("CanceladaPelaEquipe", estado.Situacao);
            Assert.Equal(Motivo, estado.Motivo);
            Assert.Equal(c.Equipe.Id, estado.CanceladaPorId);
            Assert.InRange(estado.CanceladaEm!.Value, antes.AddSeconds(-1), TimeProvider.System.GetUtcNow().AddSeconds(1));
        }

        await AssertAtivaAsync(foraDoIntervalo.Id, deOutraMaquina.Id, demanda.Id);

        // O dono vê o cancelamento, o motivo, quem desmarcou e quando (§4.3).
        var minhas = await ListarAsync(doDono, "/me/reservas");
        var vista = Assert.Single(minhas, r => r.Id == c.Pessoal.Id);
        Assert.Equal(SituacaoDaReserva.CanceladaPelaEquipe, vista.Situacao);
        Assert.Equal(Motivo, vista.MotivoDoCancelamento);
        Assert.Equal("Pessoa de Teste", vista.CanceladaPorNome);
        Assert.NotNull(vista.CanceladaEm);
        Assert.Equal(SituacaoDaReserva.Ativa, Assert.Single(minhas, r => r.Id == foraDoIntervalo.Id).Situacao);
    }

    [Fact]
    public async Task Desmarcar_so_cancela_as_reservas_ativas_e_a_demanda_nao_conflita_com_as_canceladas()
    {
        var c = await MontarAsync();
        using var doDono = fabrica.Contas.ClienteCom(c.Dono.Token);
        // O dono cancela antes: o conflito sumiu, e a demanda é criada ignorando o motivo.
        Assert.Equal(HttpStatusCode.OK, (await CancelarAsync(doDono, c.Pessoal.Id)).StatusCode);

        var resposta = await PedirDemandaAsync(c.Equipe, c.Maquina.Id, Inicio, Fim, Motivo);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var estado = await EstadoAsync(c.Pessoal.Id);
        Assert.Equal("CanceladaPeloDono", estado.Situacao);
        Assert.Null(estado.Motivo);
    }

    // Item 8: a lista de máquinas livres em todo 409 de agenda do POST /reservas.
    [Fact]
    public async Task O_409_de_uma_pessoal_traz_as_maquinas_livres_e_a_mesma_lista_da_disponibilidade()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var ocupada = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "A ocupada");
        var livreB = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "C livre");
        var livreA = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "B livre");
        await _reservas.CriarMaquinaAsync(tipo.Id, ativo: false, prefixo: "D inativa");
        var ana = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var beto = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        using var daEquipe = fabrica.Contas.ClienteCom(beto.Token);
        using var daAna = fabrica.Contas.ClienteCom(ana.Token);
        await ReservarOkAsync(daEquipe, ocupada.Id, Inicio, Fim, "DemandaDoParque", "Prof. Daniel");

        var resposta = await ReservarAsync(daAna, ocupada.Id, Inicio, Inicio.AddHours(1));

        using var corpo = await LerConflitoAsync(resposta);
        var detalhe = Detalhe(corpo);
        Assert.Equal(Conflito, detalhe);
        var livres = corpo.RootElement.GetProperty("maquinasLivres").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([livreA.Id, livreB.Id], livres);

        var daDisponibilidade = await daAna.GetAsync(
            $"/disponibilidade?tipoId={tipo.Id}&inicio={Horario(Inicio)}&fim={Horario(Inicio.AddHours(1))}",
            TestContext.Current.CancellationToken);
        var disponiveis = await Json.LerAsync<List<EquipamentoResposta>>(daDisponibilidade);
        Assert.Equal(disponiveis.Select(m => m.Id), livres);

        // A demanda sem motivo também vê a máquina livre, antes de digitar um motivo que seria recusado.
        using var semMotivo = await LerConflitoAsync(
            await PedirDemandaAsync(beto, ocupada.Id, Inicio, Fim, motivo: null));
        Assert.Equal(2, semMotivo.RootElement.GetProperty("maquinasLivres").GetArrayLength());
    }

    [Fact]
    public async Task O_409_sem_maquina_livre_traz_a_lista_vazia()
    {
        var c = await MontarAsync();
        using var doDono = fabrica.Contas.ClienteCom(c.Dono.Token);
        // Uma máquina só no tipo e ocupada: a lista existe e vem vazia.
        var resposta = await ReservarAsync(doDono, c.Maquina.Id, Inicio, Inicio.AddHours(1));

        using var corpo = await LerConflitoAsync(resposta);
        var lista = corpo.RootElement.GetProperty("maquinasLivres");
        Assert.Equal(JsonValueKind.Array, lista.ValueKind);
        Assert.Equal(0, lista.GetArrayLength());
    }

    // Item 12.
    [Fact]
    public async Task Motivo_num_projeto_pessoal_responde_400_no_campo()
    {
        var c = await MontarAsync();
        var outroDono = await _reservas.PessoaTreinadaAsync(c.Tipo.Id);
        using var cliente = fabrica.Contas.ClienteCom(outroDono.Token);

        var resposta = await ReservarAsync(
            cliente, c.Maquina.Id, Inicio.AddHours(5), Inicio.AddHours(6), motivoDesmarcacao: Motivo);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.True(corpo.RootElement.GetProperty("errors").TryGetProperty("motivoDesmarcacao", out _));
    }

    [Fact]
    public async Task Motivo_so_com_espacos_num_projeto_pessoal_vale_como_ausente()
    {
        var c = await MontarAsync();
        var outroDono = await _reservas.PessoaTreinadaAsync(c.Tipo.Id);
        using var cliente = fabrica.Contas.ClienteCom(outroDono.Token);

        var resposta = await ReservarAsync(
            cliente, c.Maquina.Id, Inicio.AddHours(5), Inicio.AddHours(6), motivoDesmarcacao: "   ");

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
    }

    [Fact]
    public async Task Motivo_com_mais_de_500_caracteres_responde_400_e_com_500_passa()
    {
        var c = await MontarAsync();

        var longo = await PedirDemandaAsync(c.Equipe, c.Maquina.Id, Inicio.AddHours(5), Inicio.AddHours(6), new string('x', 501));
        var limite = await PedirDemandaAsync(c.Equipe, c.Maquina.Id, Inicio.AddHours(5), Inicio.AddHours(6), new string('x', 500));

        Assert.Equal(HttpStatusCode.BadRequest, longo.StatusCode);
        using var corpo = await Json.DocumentoAsync(longo);
        Assert.True(corpo.RootElement.GetProperty("errors").TryGetProperty("motivoDesmarcacao", out _));
        // Sem conflito, o motivo é ignorado: a demanda é criada.
        Assert.Equal(HttpStatusCode.Created, limite.StatusCode);
    }

    [Fact]
    public async Task Usuario_com_demanda_e_motivo_responde_403_e_nada_e_cancelado()
    {
        var c = await MontarAsync();
        var outroDono = await _reservas.PessoaTreinadaAsync(c.Tipo.Id);
        using var cliente = fabrica.Contas.ClienteCom(outroDono.Token);

        // A regra 7 (papel) vem antes do 409, com ou sem motivo.
        var resposta = await ReservarAsync(
            cliente, c.Maquina.Id, Inicio, Fim, "DemandaDoParque", motivoDesmarcacao: Motivo);

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        await AssertAtivaAsync(c.Pessoal.Id);
    }

    [Fact]
    public async Task Demanda_sem_conflito_com_motivo_e_criada_e_nao_mexe_em_nada()
    {
        var c = await MontarAsync();

        var resposta = await PedirDemandaAsync(c.Equipe, c.Maquina.Id, Inicio.AddHours(5), Inicio.AddHours(6), Motivo);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        await AssertAtivaAsync(c.Pessoal.Id);
    }
}
