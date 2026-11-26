using System.Net;
using System.Net.Http.Json;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Contratos.Treinamentos;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// Monta o que os testes de reserva precisam: tipos, máquinas, pessoas treinadas e os pedidos. Cada teste cria
/// as próprias coisas (nomes com Guid), porque os testes da coleção compartilham o banco.
/// A cada chamada a Equipe e a Coordenação entram de novo, porque o token dura 60 minutos e os testes com o
/// relógio falso passam disso.
/// </summary>
public sealed class AuxiliarDeReservas(AuxiliarDeContas contas)
{
    private ContaDeTeste? _equipe;

    public async Task<TipoDeEquipamentoResposta> CriarTipoAsync(int minutosMaximos = 120)
    {
        using var coordenacao = await ClienteDaCoordenacaoAsync();
        var resposta = await coordenacao.PostAsJsonAsync(
            "/tipos-de-equipamento",
            new { nome = $"Tipo {Guid.NewGuid():N}", duracaoMaximaPessoalMinutos = minutosMaximos },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await Json.LerAsync<TipoDeEquipamentoResposta>(resposta);
    }

    public async Task<EquipamentoResposta> CriarMaquinaAsync(Guid tipoId, bool ativo = true, string prefixo = "Máquina")
    {
        using var coordenacao = await ClienteDaCoordenacaoAsync();
        var resposta = await coordenacao.PostAsJsonAsync(
            "/equipamentos",
            new { nome = $"{prefixo} {Guid.NewGuid():N}", tipoId, ativo },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await Json.LerAsync<EquipamentoResposta>(resposta);
    }

    /// <summary>Uma pessoa da Equipe do cenário (a mesma em todas as chamadas), que dá os treinamentos.</summary>
    public async Task<ContaDeTeste> EquipeAsync() => _equipe ??= await contas.CriarAsync(Papel.Equipe);

    public async Task<HttpClient> ClienteDaEquipeAsync()
    {
        var equipe = await EquipeAsync();
        return contas.ClienteCom(await contas.EntrarAsync(equipe.Email, equipe.Senha));
    }

    public async Task<HttpClient> ClienteDaCoordenacaoAsync()
    {
        var coordenacao = await contas.CoordenacaoInicialAsync();
        return contas.ClienteCom(await contas.EntrarAsync(coordenacao.Email, coordenacao.Senha));
    }

    /// <summary>Entra de novo e devolve um cliente com um token novo.</summary>
    public async Task<HttpClient> ClienteAsync(ContaDeTeste conta) =>
        contas.ClienteCom(await contas.EntrarAsync(conta.Email, conta.Senha));

    public async Task<TreinamentoResposta> TreinarAsync(Guid usuarioId, Guid tipoId)
    {
        using var equipe = await ClienteDaEquipeAsync();
        var resposta = await equipe.PostAsJsonAsync(
            "/treinamentos", new { usuarioId, tipoId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await Json.LerAsync<TreinamentoResposta>(resposta);
    }

    public async Task<HttpResponseMessage> RevogarAsync(Guid treinamentoId, string motivo = "Motivo de teste.")
    {
        using var equipe = await ClienteDaEquipeAsync();
        return await equipe.PostAsJsonAsync(
            $"/treinamentos/{treinamentoId}/revogacao", new { motivo }, TestContext.Current.CancellationToken);
    }

    /// <summary>Cria uma pessoa do papel pedido e a treina no tipo (a Equipe registra o treinamento).</summary>
    public async Task<ContaDeTeste> PessoaTreinadaAsync(Guid tipoId, Papel papel = Papel.Usuario)
    {
        var pessoa = await contas.CriarAsync(papel);
        await TreinarAsync(pessoa.Id, tipoId);
        return pessoa;
    }

    public static Task<HttpResponseMessage> ReservarAsync(
        HttpClient cliente,
        Guid equipamentoId,
        DateTimeOffset inicio,
        DateTimeOffset fim,
        string finalidade = "ProjetoPessoal",
        string? solicitante = null,
        string? descricao = null,
        string? motivoDesmarcacao = null) =>
        cliente.PostAsJsonAsync(
            "/reservas",
            new { equipamentoId, inicio, fim, finalidade, solicitante, descricao, motivoDesmarcacao },
            TestContext.Current.CancellationToken);

    public static async Task<ReservaResposta> ReservarOkAsync(
        HttpClient cliente,
        Guid equipamentoId,
        DateTimeOffset inicio,
        DateTimeOffset fim,
        string finalidade = "ProjetoPessoal",
        string? solicitante = null)
    {
        var resposta = await ReservarAsync(cliente, equipamentoId, inicio, fim, finalidade, solicitante);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await Json.LerAsync<ReservaResposta>(resposta);
    }

    public static Task<HttpResponseMessage> CancelarAsync(HttpClient cliente, Guid reservaId) =>
        cliente.PostAsync($"/reservas/{reservaId}/cancelamento", content: null, TestContext.Current.CancellationToken);

    public static async Task<List<ReservaResposta>> ListarAsync(HttpClient cliente, string rota)
    {
        var resposta = await cliente.GetAsync(rota, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return await Json.LerAsync<List<ReservaResposta>>(resposta);
    }

    /// <summary>Meia-noite UTC de hoje mais os dias, para montar horários redondos (<c>Dia(3).AddHours(10)</c>).</summary>
    public static DateTimeOffset Dia(int dias) =>
        new DateTimeOffset(TimeProvider.System.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero).AddDays(dias);

    /// <summary>O horário em UTC com <c>Z</c>, pronto para a query (sem o <c>+</c>, que a URL leria como espaço).</summary>
    public static string Horario(DateTimeOffset instante) =>
        Uri.EscapeDataString(instante.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

    /// <summary>O instante de hoje mais o deslocamento, com segundos cheios (o PostgreSQL guarda microssegundos).</summary>
    public static DateTimeOffset Daqui(TimeSpan deslocamento)
    {
        var agora = TimeProvider.System.GetUtcNow();
        return new DateTimeOffset(agora.Year, agora.Month, agora.Day, agora.Hour, agora.Minute, agora.Second, TimeSpan.Zero)
            + deslocamento;
    }
}
