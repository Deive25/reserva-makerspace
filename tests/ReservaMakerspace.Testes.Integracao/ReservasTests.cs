using System.Net;
using System.Net.Http.Json;
using System.Text;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// Criar, consultar, listar e cancelar reservas, no banco compartilhado e com o relógio do sistema. Cada teste cria as
/// próprias pessoas, o próprio tipo e as próprias máquinas, porque a coleção compartilha o banco. As bordas que
/// dependem do instante exato ficam em <see cref="ReservasComRelogioTests"/> (relógio falso).
/// </summary>
[Collection(ApiCollection.Nome)]
public class ReservasTests(FabricaDaApi fabrica)
{
    private readonly AuxiliarDeReservas _reservas = new(fabrica.Contas);

    // Amanhã, com segundos cheios.
    private static DateTimeOffset Amanha => Daqui(TimeSpan.FromDays(1));

    private static async Task ConferirProblemaAsync(HttpResponseMessage resposta, HttpStatusCode status, string titulo, string? detalhe = null)
    {
        Assert.Equal(status, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal(titulo, corpo.RootElement.GetProperty("title").GetString());
        if (detalhe is not null)
        {
            Assert.Equal(detalhe, corpo.RootElement.GetProperty("detail").GetString());
        }
    }

    private static async Task ConferirErroDeCampoAsync(HttpResponseMessage resposta, string campo)
    {
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.True(
            corpo.RootElement.GetProperty("errors").TryGetProperty(campo, out _),
            $"Faltou o erro em errors.{campo}: {corpo.RootElement}");
    }

    private sealed record Cenario(
        ReservaMakerspace.Contratos.Equipamentos.TipoDeEquipamentoResposta Tipo,
        ReservaMakerspace.Contratos.Equipamentos.EquipamentoResposta Maquina,
        ContaDeTeste Pessoa);

    private async Task<Cenario> MontarAsync(int minutosMaximos = 120, Papel papel = Papel.Usuario)
    {
        var tipo = await _reservas.CriarTipoAsync(minutosMaximos);
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var pessoa = await _reservas.PessoaTreinadaAsync(tipo.Id, papel);
        return new Cenario(tipo, maquina, pessoa);
    }

    // ---- Criar, consultar ----

    [Fact]
    public async Task Reservar_responde_201_com_location_o_operador_de_quem_pediu_e_a_resposta_completa()
    {
        var c = await MontarAsync();
        using var cliente = await _reservas.ClienteAsync(c.Pessoa);
        var inicio = Amanha;

        var resposta = await ReservarAsync(cliente, c.Maquina.Id, inicio, inicio.AddHours(2), descricao: "  Suporte de celular  ");

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criada = await Json.LerAsync<ReservaResposta>(resposta);
        Assert.Equal($"/reservas/{criada.Id}", resposta.Headers.Location?.OriginalString);
        Assert.Equal(c.Maquina.Id, criada.EquipamentoId);
        Assert.Equal(c.Maquina.Nome, criada.EquipamentoNome);
        Assert.Equal(c.Tipo.Nome, criada.TipoNome);
        Assert.Equal(c.Pessoa.Id, criada.OperadorId);
        Assert.Equal("Pessoa de Teste", criada.OperadorNome);
        Assert.Equal(inicio, criada.Inicio);
        Assert.Equal(TimeSpan.Zero, criada.Inicio.Offset);
        Assert.Equal(inicio.AddHours(2), criada.Fim);
        Assert.Equal(FinalidadeDaReserva.ProjetoPessoal, criada.Finalidade);
        Assert.Null(criada.Solicitante);
        Assert.Equal("Suporte de celular", criada.Descricao);
        Assert.Equal(SituacaoDaReserva.Ativa, criada.Situacao);
        Assert.Null(criada.CanceladaEm);
        Assert.Null(criada.CanceladaPorNome);
        Assert.Null(criada.MotivoDoCancelamento);
        Assert.True(criada.CriadaEm > Daqui(TimeSpan.FromMinutes(-5)));

        // O Location aponta para uma rota que existe, e o dono lê a mesma reserva.
        var lida = await cliente.GetAsync(resposta.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, lida.StatusCode);
        Assert.Equal(criada, await Json.LerAsync<ReservaResposta>(lida));
    }

    [Fact]
    public async Task Horario_com_fuso_e_gravado_e_devolvido_em_UTC()
    {
        var c = await MontarAsync();
        using var cliente = await _reservas.ClienteAsync(c.Pessoa);
        var inicio = Amanha;
        var emBrasilia = inicio.ToOffset(TimeSpan.FromHours(-3));

        var resposta = await ReservarAsync(cliente, c.Maquina.Id, emBrasilia, emBrasilia.AddHours(1));

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criada = await Json.LerAsync<ReservaResposta>(resposta);
        Assert.Equal(TimeSpan.Zero, criada.Inicio.Offset);
        Assert.Equal(inicio, criada.Inicio);
        Assert.Equal(inicio.AddHours(1), criada.Fim);

        // A mesma reserva, lida de novo do banco, continua em UTC.
        var lida = await Json.LerAsync<ReservaResposta>(
            await cliente.GetAsync($"/reservas/{criada.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.Zero, lida.Inicio.Offset);
        Assert.Equal(inicio, lida.Inicio);
    }

    [Theory]
    [InlineData("""{"equipamentoId":"{m}","inicio":"2099-12-01T10:00:00","fim":"2099-12-01T11:00:00Z","finalidade":"ProjetoPessoal"}""", "inicio")]
    [InlineData("""{"equipamentoId":"{m}","inicio":"2099-12-01T10:00:00Z","fim":"2099-12-01T11:00:00","finalidade":"ProjetoPessoal"}""", "fim")]
    [InlineData("""{"equipamentoId":"{m}","inicio":"2099-12-01","fim":"2099-12-01T11:00:00Z","finalidade":"ProjetoPessoal"}""", "inicio")]
    [InlineData("""{"equipamentoId":"{m}","inicio":"amanhã","fim":"2099-12-01T11:00:00Z","finalidade":"ProjetoPessoal"}""", "inicio")]
    [InlineData("""{"equipamentoId":"{m}","inicio":1796119200,"fim":"2099-12-01T11:00:00Z","finalidade":"ProjetoPessoal"}""", "inicio")]
    public async Task Horario_sem_fuso_ou_mal_formado_responde_400_no_campo(string modelo, string campo)
    {
        var c = await MontarAsync();
        using var cliente = await _reservas.ClienteAsync(c.Pessoa);
        using var conteudo = new StringContent(modelo.Replace("{m}", c.Maquina.Id.ToString()), Encoding.UTF8, "application/json");

        var resposta = await cliente.PostAsync("/reservas", conteudo, TestContext.Current.CancellationToken);

        // Erro de leitura do JSON: a chave segue o caminho do JSON ("$.inicio"), como nos outros erros desse tipo da API.
        await ConferirErroDeCampoAsync(resposta, "$." + campo);
    }

    [Fact]
    public async Task Pedido_sem_campos_obrigatorios_ou_com_finalidade_invalida_responde_400_no_campo()
    {
        var c = await MontarAsync();
        using var cliente = await _reservas.ClienteAsync(c.Pessoa);

        var vazio = await cliente.PostAsync(
            "/reservas", new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, vazio.StatusCode);
        using (var corpo = await Json.DocumentoAsync(vazio))
        {
            var erros = corpo.RootElement.GetProperty("errors");
            foreach (var campo in new[] { "equipamentoId", "inicio", "fim", "finalidade" })
            {
                Assert.True(erros.TryGetProperty(campo, out _), $"Faltou errors.{campo}.");
            }
        }

        using var invalida = new StringContent(
            $$"""{"equipamentoId":"{{c.Maquina.Id}}","inicio":"2099-12-01T10:00:00Z","fim":"2099-12-01T11:00:00Z","finalidade":"Outra"}""",
            Encoding.UTF8,
            "application/json");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await cliente.PostAsync("/reservas", invalida, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Validacoes_de_formato_respondem_400_no_campo_e_nao_gravam_nada()
    {
        var c = await MontarAsync();
        using var cliente = await _reservas.ClienteAsync(c.Pessoa);
        var inicio = Amanha;

        // fim <= início
        await ConferirErroDeCampoAsync(await ReservarAsync(cliente, c.Maquina.Id, inicio, inicio), "fim");
        await ConferirErroDeCampoAsync(await ReservarAsync(cliente, c.Maquina.Id, inicio, inicio.AddMinutes(-1)), "fim");
        // solicitante num projeto pessoal
        await ConferirErroDeCampoAsync(
            await ReservarAsync(cliente, c.Maquina.Id, inicio, inicio.AddHours(1), solicitante: "Prof. Daniel"), "solicitante");
        // máquina inexistente (400 no corpo; o 404 é do id da URL)
        await ConferirErroDeCampoAsync(
            await ReservarAsync(cliente, Guid.NewGuid(), inicio, inicio.AddHours(1)), "equipamentoId");
        // textos longos demais
        await ConferirErroDeCampoAsync(
            await ReservarAsync(cliente, c.Maquina.Id, inicio, inicio.AddHours(1), descricao: new string('a', 501)), "descricao");
        using var equipe = await _reservas.ClienteDaEquipeAsync();
        await ConferirErroDeCampoAsync(
            await ReservarAsync(equipe, c.Maquina.Id, inicio, inicio.AddHours(1), "DemandaDoParque", new string('a', 101)),
            "solicitante");

        Assert.Empty(await ListarAsync(cliente, "/me/reservas"));
    }

    [Fact]
    public async Task Texto_so_com_espacos_vira_nulo_na_demanda_do_parque()
    {
        var c = await MontarAsync(papel: Papel.Equipe);
        using var cliente = await _reservas.ClienteAsync(c.Pessoa);
        var inicio = Amanha;

        var resposta = await ReservarAsync(
            cliente, c.Maquina.Id, inicio, inicio.AddHours(1), "DemandaDoParque", solicitante: "   ", descricao: "   ");

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criada = await Json.LerAsync<ReservaResposta>(resposta);
        Assert.Null(criada.Solicitante);
        Assert.Null(criada.Descricao);
    }

    // ---- Regras da §4.2 (422 e 403) ----

    [Fact]
    public async Task Cada_regra_de_negocio_responde_422_com_a_mensagem_da_regra()
    {
        var c = await MontarAsync(minutosMaximos: 120);
        using var cliente = await _reservas.ClienteAsync(c.Pessoa);
        var inicio = Amanha;

        // Regra 1: início no passado.
        await ConferirProblemaAsync(
            await ReservarAsync(cliente, c.Maquina.Id, Daqui(TimeSpan.FromHours(-2)), Daqui(TimeSpan.FromHours(-1))),
            HttpStatusCode.UnprocessableEntity, "Regra de negócio violada", "O início da reserva não pode ser no passado.");

        // Regra 2: máquina inativa.
        var inativa = await _reservas.CriarMaquinaAsync(c.Tipo.Id, ativo: false);
        await ConferirProblemaAsync(
            await ReservarAsync(cliente, inativa.Id, inicio, inicio.AddHours(1)),
            HttpStatusCode.UnprocessableEntity, "Regra de negócio violada", $"A máquina {inativa.Nome} está inativa.");

        // Regra 5: duração acima da máxima do tipo (a máxima exata passa).
        await ConferirProblemaAsync(
            await ReservarAsync(cliente, c.Maquina.Id, inicio, inicio.AddMinutes(121)),
            HttpStatusCode.UnprocessableEntity, "Regra de negócio violada",
            $"A duração máxima de uma reserva pessoal em {c.Tipo.Nome} é 2 h.");
        Assert.Equal(
            HttpStatusCode.Created,
            (await ReservarAsync(cliente, c.Maquina.Id, inicio, inicio.AddMinutes(120))).StatusCode);

        // Regra 5: início além de 7 dias.
        var outra = await _reservas.CriarMaquinaAsync(c.Tipo.Id);
        var alem = Daqui(TimeSpan.FromDays(7) + TimeSpan.FromHours(1));
        await ConferirProblemaAsync(
            await ReservarAsync(cliente, outra.Id, alem, alem.AddHours(1)),
            HttpStatusCode.UnprocessableEntity, "Regra de negócio violada", "Uma reserva pessoal só pode começar em até 7 dias.");
    }

    [Fact]
    public async Task Sem_treinamento_valido_no_tipo_da_maquina_responde_422_nos_tres_casos()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var inicio = Amanha;
        var mensagem = $"Sem treinamento válido em {tipo.Nome} até o fim da reserva.";

        // Nenhum treinamento.
        var semNada = await fabrica.Contas.CriarAsync(Papel.Usuario);
        using (var cliente = await _reservas.ClienteAsync(semNada))
        {
            await ConferirProblemaAsync(
                await ReservarAsync(cliente, maquina.Id, inicio, inicio.AddHours(1)),
                HttpStatusCode.UnprocessableEntity, "Regra de negócio violada", mensagem);
        }

        // Treinamento de outro tipo.
        var outroTipo = await _reservas.CriarTipoAsync();
        var deOutroTipo = await _reservas.PessoaTreinadaAsync(outroTipo.Id);
        using (var cliente = await _reservas.ClienteAsync(deOutroTipo))
        {
            await ConferirProblemaAsync(
                await ReservarAsync(cliente, maquina.Id, inicio, inicio.AddHours(1)),
                HttpStatusCode.UnprocessableEntity, "Regra de negócio violada", mensagem);
        }

        // Treinamento revogado (o vencido fica em ReservasComRelogioTests, que avança o relógio).
        var revogada = await _reservas.CriarTipoAsync();
        var maquinaDaRevogada = await _reservas.CriarMaquinaAsync(revogada.Id);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var treinamento = await _reservas.TreinarAsync(pessoa.Id, revogada.Id);
        Assert.Equal(HttpStatusCode.OK, (await _reservas.RevogarAsync(treinamento.Id)).StatusCode);
        using (var cliente = await _reservas.ClienteAsync(pessoa))
        {
            await ConferirProblemaAsync(
                await ReservarAsync(cliente, maquinaDaRevogada.Id, inicio, inicio.AddHours(1)),
                HttpStatusCode.UnprocessableEntity, "Regra de negócio violada",
                $"Sem treinamento válido em {revogada.Nome} até o fim da reserva.");
        }
    }

    [Fact]
    public async Task Terceira_reserva_pessoal_responde_422_e_a_demanda_do_parque_nao_conta_no_limite()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var a = await _reservas.CriarMaquinaAsync(tipo.Id);
        var b = await _reservas.CriarMaquinaAsync(tipo.Id);
        var c = await _reservas.CriarMaquinaAsync(tipo.Id);
        var pessoa = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        using var cliente = await _reservas.ClienteAsync(pessoa);
        var inicio = Amanha;

        // Uma demanda do parque antes não entra na conta.
        await ReservarOkAsync(cliente, a.Id, inicio, inicio.AddHours(1), "DemandaDoParque", "Prof. Daniel");
        await ReservarOkAsync(cliente, a.Id, inicio.AddHours(2), inicio.AddHours(3));
        await ReservarOkAsync(cliente, b.Id, inicio, inicio.AddHours(1));

        await ConferirProblemaAsync(
            await ReservarAsync(cliente, c.Id, inicio, inicio.AddHours(1)),
            HttpStatusCode.UnprocessableEntity, "Regra de negócio violada",
            "Você já tem 2 reservas pessoais ativas. Cancele uma ou espere uma acabar.");

        // Uma demanda continua passando com o limite pessoal cheio.
        await ReservarOkAsync(cliente, c.Id, inicio, inicio.AddHours(5), "DemandaDoParque");

        // Cancelar uma pessoal libera a vaga.
        var minhas = await ListarAsync(cliente, "/me/reservas");
        var pessoalDaB = Assert.Single(minhas, r => r.EquipamentoId == b.Id);
        Assert.Equal(HttpStatusCode.OK, (await CancelarAsync(cliente, pessoalDaB.Id)).StatusCode);
        await ReservarOkAsync(cliente, c.Id, inicio.AddHours(6), inicio.AddHours(7));
    }

    [Fact]
    public async Task Usuario_pedindo_demanda_do_parque_responde_403_e_a_Equipe_e_a_Coordenacao_conseguem()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var a = await _reservas.CriarMaquinaAsync(tipo.Id);
        var b = await _reservas.CriarMaquinaAsync(tipo.Id);
        var usuario = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var equipe = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        var coordenacao = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Coordenacao);
        var inicio = Amanha;

        using (var cliente = await _reservas.ClienteAsync(usuario))
        {
            await ConferirProblemaAsync(
                await ReservarAsync(cliente, a.Id, inicio, inicio.AddHours(1), "DemandaDoParque"),
                HttpStatusCode.Forbidden, "Acesso negado");
        }

        using (var cliente = await _reservas.ClienteAsync(equipe))
        {
            var demanda = await ReservarOkAsync(
                cliente, a.Id, inicio, inicio.AddHours(5), "DemandaDoParque", "Prof. Daniel (TCC de Engenharia)");
            Assert.Equal(FinalidadeDaReserva.DemandaDoParque, demanda.Finalidade);
            Assert.Equal("Prof. Daniel (TCC de Engenharia)", demanda.Solicitante);
        }

        using (var cliente = await _reservas.ClienteAsync(coordenacao))
        {
            await ReservarOkAsync(cliente, b.Id, inicio, inicio.AddHours(5), "DemandaDoParque");
        }
    }

    [Fact]
    public async Task Demanda_do_parque_comeca_em_ate_30_dias_e_o_limite_de_7_dias_e_so_do_projeto_pessoal()
    {
        var c = await MontarAsync(papel: Papel.Equipe);
        using var cliente = await _reservas.ClienteAsync(c.Pessoa);
        var em20Dias = Daqui(TimeSpan.FromDays(20));
        var em31Dias = Daqui(TimeSpan.FromDays(31));

        await ReservarOkAsync(cliente, c.Maquina.Id, em20Dias, em20Dias.AddHours(1), "DemandaDoParque");

        await ConferirProblemaAsync(
            await ReservarAsync(cliente, c.Maquina.Id, em31Dias, em31Dias.AddHours(1), "DemandaDoParque"),
            HttpStatusCode.UnprocessableEntity, "Regra de negócio violada",
            "Uma demanda do parque só pode começar em até 30 dias.");
    }

    // ---- Conflitos (regra 4) ----

    [Fact]
    public async Task Horario_cruzado_na_mesma_maquina_responde_409_com_a_lista_sem_o_nome_do_operador()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var primeira = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        var segunda = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var inicio = Amanha;
        using (var cliente = await _reservas.ClienteAsync(primeira))
        {
            await ReservarOkAsync(cliente, maquina.Id, inicio, inicio.AddHours(2), "DemandaDoParque", "Prof. Daniel");
        }

        using var outro = await _reservas.ClienteAsync(segunda);
        var resposta = await ReservarAsync(outro, maquina.Id, inicio.AddHours(1), inicio.AddHours(2));

        await ConferirProblemaAsync(resposta, HttpStatusCode.Conflict, "Conflito");
        using var corpo = await Json.DocumentoAsync(resposta);
        var conflito = Assert.Single(corpo.RootElement.GetProperty("conflitos").EnumerateArray());
        Assert.Equal(inicio, conflito.GetProperty("inicio").GetDateTimeOffset());
        Assert.Equal(inicio.AddHours(2), conflito.GetProperty("fim").GetDateTimeOffset());
        Assert.Equal("DemandaDoParque", conflito.GetProperty("finalidade").GetString());
        // Sem dados da pessoa: só o horário e a finalidade.
        Assert.Equal(3, conflito.EnumerateObject().Count());
        Assert.DoesNotContain("Prof. Daniel", corpo.RootElement.ToString());
        Assert.Empty(await ListarAsync(outro, "/me/reservas"));
    }

    [Fact]
    public async Task Conflito_com_duas_reservas_lista_as_duas_em_ordem_de_inicio()
    {
        var c = await MontarAsync(minutosMaximos: 600, papel: Papel.Equipe);
        using var cliente = await _reservas.ClienteAsync(c.Pessoa);
        var inicio = Amanha;
        await ReservarOkAsync(cliente, c.Maquina.Id, inicio.AddHours(4), inicio.AddHours(5), "DemandaDoParque");
        await ReservarOkAsync(cliente, c.Maquina.Id, inicio, inicio.AddHours(1), "DemandaDoParque");

        var resposta = await ReservarAsync(cliente, c.Maquina.Id, inicio.AddMinutes(30), inicio.AddHours(6));

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        var inicios = corpo.RootElement.GetProperty("conflitos").EnumerateArray()
            .Select(e => e.GetProperty("inicio").GetDateTimeOffset()).ToList();
        Assert.Equal([inicio, inicio.AddHours(4)], inicios);
    }

    [Fact]
    public async Task Reservas_encostadas_outra_maquina_e_cima_de_cancelada_sao_aceitas()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var outra = await _reservas.CriarMaquinaAsync(tipo.Id);
        var pessoa = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        using var cliente = await _reservas.ClienteAsync(pessoa);
        var inicio = Amanha;
        var base10 = await ReservarOkAsync(cliente, maquina.Id, inicio, inicio.AddHours(1), "DemandaDoParque");

        // Uma acaba às 10:00 e a outra começa às 10:00: não conflita, nos dois sentidos.
        await ReservarOkAsync(cliente, maquina.Id, inicio.AddHours(1), inicio.AddHours(2), "DemandaDoParque");
        await ReservarOkAsync(cliente, maquina.Id, inicio.AddHours(-1), inicio, "DemandaDoParque");
        // Outra máquina no mesmo horário.
        await ReservarOkAsync(cliente, outra.Id, inicio, inicio.AddHours(1), "DemandaDoParque");

        // Por cima de uma reserva cancelada.
        var pessoal = await ReservarOkAsync(cliente, maquina.Id, inicio.AddHours(10), inicio.AddHours(11));
        Assert.Equal(HttpStatusCode.OK, (await CancelarAsync(cliente, pessoal.Id)).StatusCode);
        await ReservarOkAsync(cliente, maquina.Id, inicio.AddHours(10), inicio.AddHours(11));

        Assert.NotEqual(Guid.Empty, base10.Id);
    }

    // ---- Cancelar ----

    [Fact]
    public async Task O_dono_cancela_antes_do_inicio_e_a_reserva_continua_na_lista_com_a_situacao()
    {
        var c = await MontarAsync();
        using var cliente = await _reservas.ClienteAsync(c.Pessoa);
        var inicio = Amanha;
        var criada = await ReservarOkAsync(cliente, c.Maquina.Id, inicio, inicio.AddHours(1));

        var resposta = await CancelarAsync(cliente, criada.Id);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var cancelada = await Json.LerAsync<ReservaResposta>(resposta);
        Assert.Equal(criada.Id, cancelada.Id);
        Assert.Equal(SituacaoDaReserva.CanceladaPeloDono, cancelada.Situacao);
        Assert.NotNull(cancelada.CanceladaEm);
        Assert.Equal("Pessoa de Teste", cancelada.CanceladaPorNome);
        Assert.Null(cancelada.MotivoDoCancelamento);
        var minhas = await ListarAsync(cliente, "/me/reservas");
        Assert.Equal(SituacaoDaReserva.CanceladaPeloDono, Assert.Single(minhas).Situacao);

        // A máquina volta a ficar livre no horário.
        using var outra = await _reservas.ClienteAsync(await _reservas.PessoaTreinadaAsync(c.Tipo.Id));
        await ReservarOkAsync(outra, c.Maquina.Id, inicio, inicio.AddHours(1));
    }

    [Fact]
    public async Task Cancelar_de_novo_responde_409_e_id_inexistente_responde_404()
    {
        var c = await MontarAsync();
        using var cliente = await _reservas.ClienteAsync(c.Pessoa);
        var inicio = Amanha;
        var criada = await ReservarOkAsync(cliente, c.Maquina.Id, inicio, inicio.AddHours(1));
        Assert.Equal(HttpStatusCode.OK, (await CancelarAsync(cliente, criada.Id)).StatusCode);

        await ConferirProblemaAsync(
            await CancelarAsync(cliente, criada.Id), HttpStatusCode.Conflict, "Conflito", "A reserva já foi cancelada.");
        await ConferirProblemaAsync(
            await CancelarAsync(cliente, Guid.NewGuid()), HttpStatusCode.NotFound, "Recurso não encontrado", "Reserva não encontrada.");
    }

    [Fact]
    public async Task Cancelar_ou_ler_a_reserva_de_outra_pessoa_responde_403_inclusive_para_a_Equipe_no_cancelamento()
    {
        var c = await MontarAsync();
        using var dono = await _reservas.ClienteAsync(c.Pessoa);
        var inicio = Amanha;
        var criada = await ReservarOkAsync(dono, c.Maquina.Id, inicio, inicio.AddHours(1));
        var outra = await fabrica.Contas.CriarAsync(Papel.Usuario);
        using var daOutra = await _reservas.ClienteAsync(outra);
        using var daEquipe = await _reservas.ClienteDaEquipeAsync();

        await ConferirProblemaAsync(await CancelarAsync(daOutra, criada.Id), HttpStatusCode.Forbidden, "Acesso negado");
        await ConferirProblemaAsync(await CancelarAsync(daEquipe, criada.Id), HttpStatusCode.Forbidden, "Acesso negado");
        await ConferirProblemaAsync(
            await daOutra.GetAsync($"/reservas/{criada.Id}", TestContext.Current.CancellationToken),
            HttpStatusCode.Forbidden, "Acesso negado");

        // A Equipe lê a reserva de qualquer pessoa, e a reserva continua ativa.
        var lidaPelaEquipe = await daEquipe.GetAsync($"/reservas/{criada.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, lidaPelaEquipe.StatusCode);
        Assert.Equal(SituacaoDaReserva.Ativa, (await Json.LerAsync<ReservaResposta>(lidaPelaEquipe)).Situacao);
        await ConferirProblemaAsync(
            await daEquipe.GetAsync($"/reservas/{Guid.NewGuid()}", TestContext.Current.CancellationToken),
            HttpStatusCode.NotFound, "Recurso não encontrado");
    }

    // ---- Listas ----

    [Fact]
    public async Task Me_reservas_traz_so_as_da_pessoa_do_inicio_mais_recente_para_o_mais_antigo()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var ana = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var beto = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var inicio = Amanha;
        using var daAna = await _reservas.ClienteAsync(ana);
        using var doBeto = await _reservas.ClienteAsync(beto);
        var cedo = await ReservarOkAsync(daAna, maquina.Id, inicio, inicio.AddHours(1));
        var tarde = await ReservarOkAsync(daAna, maquina.Id, inicio.AddHours(5), inicio.AddHours(6));
        var doBetoReserva = await ReservarOkAsync(doBeto, maquina.Id, inicio.AddHours(2), inicio.AddHours(3));

        var dela = await ListarAsync(daAna, "/me/reservas");
        var dele = await ListarAsync(doBeto, "/me/reservas");

        Assert.Equal([tarde.Id, cedo.Id], dela.Select(r => r.Id));
        Assert.Equal([doBetoReserva.Id], dele.Select(r => r.Id));
    }

    [Fact]
    public async Task A_Equipe_lista_as_reservas_de_todos_com_os_filtros_por_pessoa_e_por_maquina()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var a = await _reservas.CriarMaquinaAsync(tipo.Id);
        var b = await _reservas.CriarMaquinaAsync(tipo.Id);
        var ana = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var beto = await _reservas.PessoaTreinadaAsync(tipo.Id);
        var inicio = Amanha;
        using var daAna = await _reservas.ClienteAsync(ana);
        using var doBeto = await _reservas.ClienteAsync(beto);
        var r1 = await ReservarOkAsync(daAna, a.Id, inicio, inicio.AddHours(1));
        var r2 = await ReservarOkAsync(daAna, b.Id, inicio.AddHours(3), inicio.AddHours(4));
        var r3 = await ReservarOkAsync(doBeto, a.Id, inicio.AddHours(5), inicio.AddHours(6));
        using var equipe = await _reservas.ClienteDaEquipeAsync();

        var todas = await ListarAsync(equipe, "/reservas");
        var daAnaApenas = await ListarAsync(equipe, $"/reservas?usuarioId={ana.Id}");
        var daMaquinaA = await ListarAsync(equipe, $"/reservas?equipamentoId={a.Id}");
        var daAnaNaA = await ListarAsync(equipe, $"/reservas?usuarioId={ana.Id}&equipamentoId={a.Id}");

        Assert.Equal([r3.Id, r2.Id, r1.Id], todas.Where(r => new[] { r1.Id, r2.Id, r3.Id }.Contains(r.Id)).Select(r => r.Id));
        Assert.Equal([r2.Id, r1.Id], daAnaApenas.Select(r => r.Id));
        Assert.Equal([r3.Id, r1.Id], daMaquinaA.Select(r => r.Id));
        Assert.Equal([r1.Id], daAnaNaA.Select(r => r.Id));
        Assert.Equal("Pessoa de Teste", daAnaApenas[0].OperadorNome);
    }

    [Fact]
    public async Task Usuario_comum_nao_lista_as_reservas_de_todos()
    {
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        using var cliente = await _reservas.ClienteAsync(pessoa);

        await ConferirProblemaAsync(
            await cliente.GetAsync("/reservas", TestContext.Current.CancellationToken),
            HttpStatusCode.Forbidden, "Acesso negado");
    }
}
