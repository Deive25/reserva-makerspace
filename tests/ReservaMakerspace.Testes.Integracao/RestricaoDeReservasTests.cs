using System.Net;
using System.Net.Http.Json;
using Npgsql;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// A garantia final contra a sobreposição (spec §6, plano do 1.5, decisão 5): a restrição de exclusão do PostgreSQL,
/// testada por SQL direto, sem passar pela API. Nenhum caminho da aplicação consegue contorná-la.
/// </summary>
[Collection(ApiCollection.Nome)]
public class RestricaoDeReservasTests(FabricaDaApi fabrica)
{
    private const string NomeDaRestricao = "ex_reservas_sem_sobreposicao";

    // Segundos cheios: o PostgreSQL guarda microssegundos.
    private static readonly DateTimeOffset Base = new(2030, 6, 10, 10, 0, 0, TimeSpan.Zero);

    private sealed record Cenario(Guid OperadorId, Guid MaquinaId, Guid OutraMaquinaId);

    private async Task<Cenario> MontarAsync()
    {
        using var coordenacao = fabrica.Contas.ClienteCom((await fabrica.Contas.CoordenacaoInicialAsync()).Token);
        var tipo = await Json.LerAsync<TipoDeEquipamentoResposta>(await coordenacao.PostAsJsonAsync(
            "/tipos-de-equipamento",
            new { nome = $"Tipo {Guid.NewGuid():N}", duracaoMaximaPessoalMinutos = 120 },
            TestContext.Current.CancellationToken));
        var maquina = await CriarMaquinaAsync(coordenacao, tipo.Id);
        var outra = await CriarMaquinaAsync(coordenacao, tipo.Id);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        return new Cenario(pessoa.Id, maquina.Id, outra.Id);
    }

    private static async Task<EquipamentoResposta> CriarMaquinaAsync(HttpClient coordenacao, Guid tipoId)
    {
        var resposta = await coordenacao.PostAsJsonAsync(
            "/equipamentos",
            new { nome = $"Máquina {Guid.NewGuid():N}", tipoId, ativo = true },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await Json.LerAsync<EquipamentoResposta>(resposta);
    }

    private async Task InserirAsync(
        Cenario c,
        Guid maquinaId,
        DateTimeOffset inicio,
        DateTimeOffset fim,
        string situacao = "Ativa")
    {
        await using var fonte = NpgsqlDataSource.Create(fabrica.ConnectionString);
        await using var comando = fonte.CreateCommand(
            """
            INSERT INTO reservas (id, equipamento_id, operador_id, inicio, fim, finalidade, situacao, criada_em)
            VALUES ($1, $2, $3, $4, $5, 'ProjetoPessoal', $6, $7)
            """);
        comando.Parameters.AddWithValue(Guid.NewGuid());
        comando.Parameters.AddWithValue(maquinaId);
        comando.Parameters.AddWithValue(c.OperadorId);
        comando.Parameters.AddWithValue(inicio);
        comando.Parameters.AddWithValue(fim);
        comando.Parameters.AddWithValue(situacao);
        comando.Parameters.AddWithValue(Base);
        await comando.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Duas_reservas_ativas_sobrepostas_na_mesma_maquina_violam_a_restricao_de_exclusao()
    {
        var c = await MontarAsync();
        await InserirAsync(c, c.MaquinaId, Base, Base.AddHours(2));

        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            InserirAsync(c, c.MaquinaId, Base.AddHours(1), Base.AddHours(3)));

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, erro.SqlState);
        Assert.Equal(NomeDaRestricao, erro.ConstraintName);

        // Contida na outra e idêntica também são sobreposição.
        Assert.Equal(
            PostgresErrorCodes.ExclusionViolation,
            (await Assert.ThrowsAsync<PostgresException>(() =>
                InserirAsync(c, c.MaquinaId, Base.AddMinutes(30), Base.AddHours(1)))).SqlState);
        Assert.Equal(
            PostgresErrorCodes.ExclusionViolation,
            (await Assert.ThrowsAsync<PostgresException>(() =>
                InserirAsync(c, c.MaquinaId, Base, Base.AddHours(2)))).SqlState);
    }

    [Fact]
    public async Task Reservas_encostadas_ou_em_outra_maquina_ou_por_cima_de_uma_cancelada_entram()
    {
        var c = await MontarAsync();
        await InserirAsync(c, c.MaquinaId, Base, Base.AddHours(2));

        // Encostadas: a que acaba às 12:00 não conflita com a que começa às 12:00 (e vice-versa).
        await InserirAsync(c, c.MaquinaId, Base.AddHours(2), Base.AddHours(3));
        await InserirAsync(c, c.MaquinaId, Base.AddHours(-1), Base);

        // Outra máquina, mesmo horário.
        await InserirAsync(c, c.OutraMaquinaId, Base, Base.AddHours(2));

        // Uma cancelada não ocupa a agenda: nem a que já existe (cancelada primeiro), nem a nova.
        await InserirAsync(c, c.MaquinaId, Base.AddHours(10), Base.AddHours(12), situacao: "CanceladaPeloDono");
        await InserirAsync(c, c.MaquinaId, Base.AddHours(10), Base.AddHours(12));
        await InserirAsync(c, c.MaquinaId, Base.AddHours(11), Base.AddHours(13), situacao: "CanceladaPelaEquipe");
    }

    [Fact]
    public async Task Reativar_uma_reserva_cancelada_por_cima_de_outra_ativa_tambem_viola_a_restricao()
    {
        var c = await MontarAsync();
        await InserirAsync(c, c.MaquinaId, Base, Base.AddHours(2));
        await InserirAsync(c, c.MaquinaId, Base.AddHours(1), Base.AddHours(3), situacao: "CanceladaPeloDono");

        await using var fonte = NpgsqlDataSource.Create(fabrica.ConnectionString);
        await using var comando = fonte.CreateCommand(
            "UPDATE reservas SET situacao = 'Ativa' WHERE equipamento_id = $1 AND situacao = 'CanceladaPeloDono'");
        comando.Parameters.AddWithValue(c.MaquinaId);
        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            comando.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, erro.SqlState);
    }

    [Fact]
    public async Task Fim_igual_ou_antes_do_inicio_viola_a_restricao_de_check()
    {
        var c = await MontarAsync();

        var igual = await Assert.ThrowsAsync<PostgresException>(() => InserirAsync(c, c.MaquinaId, Base, Base));
        var antes = await Assert.ThrowsAsync<PostgresException>(() => InserirAsync(c, c.MaquinaId, Base, Base.AddHours(-1)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, igual.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, antes.SqlState);
    }
}
