using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ReservaMakerspace.Api.Autenticacao;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

[Collection(ApiCollection.Nome)]
public class AutorizacaoTests(FabricaDaApi fabrica)
{
    private const string Emissor = "reserva-makerspace";

    public static TheoryData<string, string> RotasProtegidas => new()
    {
        { "GET", "/me" },
        { "GET", "/usuarios" },
        { "PUT", "/usuarios/00000000-0000-0000-0000-000000000001/papel" },
        { "GET", "/tipos-de-equipamento" },
        { "GET", "/tipos-de-equipamento/00000000-0000-0000-0000-000000000001" },
        { "POST", "/tipos-de-equipamento" },
        { "PUT", "/tipos-de-equipamento/00000000-0000-0000-0000-000000000001" },
        { "GET", "/equipamentos" },
        { "GET", "/equipamentos/00000000-0000-0000-0000-000000000001" },
        { "POST", "/equipamentos" },
        { "PUT", "/equipamentos/00000000-0000-0000-0000-000000000001" },
        { "POST", "/treinamentos" },
        { "GET", "/treinamentos" },
        { "GET", "/treinamentos/00000000-0000-0000-0000-000000000001" },
        { "POST", "/treinamentos/00000000-0000-0000-0000-000000000001/revogacao" },
        { "GET", "/me/treinamentos" },
    };

    // O corpo do pedido não importa aqui: o login e o papel são checados antes da validação do corpo.
    public static TheoryData<Papel, string, string> RotasDaCoordenacao => new()
    {
        { Papel.Usuario, "GET", "/usuarios" },
        { Papel.Equipe, "GET", "/usuarios" },
        { Papel.Usuario, "PUT", "/usuarios/00000000-0000-0000-0000-000000000001/papel" },
        { Papel.Equipe, "PUT", "/usuarios/00000000-0000-0000-0000-000000000001/papel" },
        { Papel.Usuario, "POST", "/tipos-de-equipamento" },
        { Papel.Equipe, "POST", "/tipos-de-equipamento" },
        { Papel.Usuario, "PUT", "/tipos-de-equipamento/00000000-0000-0000-0000-000000000001" },
        { Papel.Equipe, "PUT", "/tipos-de-equipamento/00000000-0000-0000-0000-000000000001" },
        { Papel.Usuario, "POST", "/equipamentos" },
        { Papel.Equipe, "POST", "/equipamentos" },
        { Papel.Usuario, "PUT", "/equipamentos/00000000-0000-0000-0000-000000000001" },
        { Papel.Equipe, "PUT", "/equipamentos/00000000-0000-0000-0000-000000000001" },
    };

    // As quatro rotas de treinamento da Equipe: o Usuário comum não passa. A GET /me/treinamentos é de qualquer pessoa logada.
    public static TheoryData<string, string> RotasDaEquipe => new()
    {
        { "POST", "/treinamentos" },
        { "GET", "/treinamentos" },
        { "GET", "/treinamentos/00000000-0000-0000-0000-000000000001" },
        { "POST", "/treinamentos/00000000-0000-0000-0000-000000000001/revogacao" },
    };

    private static HttpRequestMessage Pedido(string metodo, string rota, string? token = null)
    {
        var pedido = new HttpRequestMessage(new HttpMethod(metodo), rota);
        if (metodo == "PUT")
        {
            pedido.Content = JsonContent.Create(new { papel = "Equipe" });
        }

        if (token is not null)
        {
            pedido.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return pedido;
    }

    private async Task<HttpResponseMessage> EnviarAsync(string metodo, string rota, string? token)
    {
        using var cliente = fabrica.CreateClient();
        return await cliente.SendAsync(Pedido(metodo, rota, token), TestContext.Current.CancellationToken);
    }

    private static async Task ConferirProblemaAsync(HttpResponseMessage resposta, HttpStatusCode status, string titulo)
    {
        Assert.Equal(status, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal(titulo, corpo.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [MemberData(nameof(RotasProtegidas))]
    public async Task Sem_token_responde_401_em_problem_details(string metodo, string rota)
    {
        var resposta = await EnviarAsync(metodo, rota, token: null);

        await ConferirProblemaAsync(resposta, HttpStatusCode.Unauthorized, "Não autenticado");
    }

    [Theory]
    [MemberData(nameof(RotasDaCoordenacao))]
    public async Task Papel_insuficiente_responde_403_em_problem_details(Papel papel, string metodo, string rota)
    {
        var conta = await fabrica.Contas.CriarAsync(papel);

        var resposta = await EnviarAsync(metodo, rota, conta.Token);

        await ConferirProblemaAsync(resposta, HttpStatusCode.Forbidden, "Acesso negado");
    }

    [Theory]
    [MemberData(nameof(RotasDaEquipe))]
    public async Task Usuario_comum_nas_rotas_da_equipe_responde_403_em_problem_details(string metodo, string rota)
    {
        var conta = await fabrica.Contas.CriarAsync(Papel.Usuario);

        var resposta = await EnviarAsync(metodo, rota, conta.Token);

        await ConferirProblemaAsync(resposta, HttpStatusCode.Forbidden, "Acesso negado");
    }

    [Fact]
    public async Task Token_vencido_responde_401()
    {
        var conta = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var token = Emitir(conta.Id, fabrica.Chave, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));

        var resposta = await EnviarAsync("GET", "/me", token);

        await ConferirProblemaAsync(resposta, HttpStatusCode.Unauthorized, "Não autenticado");
    }

    [Fact]
    public async Task Token_assinado_com_outra_chave_responde_401()
    {
        var conta = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var token = Emitir(conta.Id, ConfiguracaoDeTeste.NovaChave(), DateTime.UtcNow, DateTime.UtcNow.AddHours(1));

        var resposta = await EnviarAsync("GET", "/me", token);

        await ConferirProblemaAsync(resposta, HttpStatusCode.Unauthorized, "Não autenticado");
    }

    [Fact]
    public async Task Token_com_alg_none_responde_401()
    {
        var conta = await fabrica.Contas.CriarAsync(Papel.Usuario);
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var cabecalho = Base64Url($$"""{"alg":"none","typ":"JWT"}""");
        var corpo = Base64Url(
            $$"""{"sub":"{{conta.Id}}","iss":"{{Emissor}}","aud":"{{Emissor}}","exp":{{exp}}}""");

        var resposta = await EnviarAsync("GET", "/me", $"{cabecalho}.{corpo}.");

        await ConferirProblemaAsync(resposta, HttpStatusCode.Unauthorized, "Não autenticado");
    }

    [Fact]
    public async Task Token_de_conta_que_nao_existe_mais_responde_401()
    {
        var conta = await fabrica.Contas.CriarAsync(Papel.Usuario);
        await using var fonte = NpgsqlDataSource.Create(fabrica.ConnectionString);
        await using var comando = fonte.CreateCommand("DELETE FROM usuarios WHERE id = $1");
        comando.Parameters.AddWithValue(conta.Id);
        await comando.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var resposta = await EnviarAsync("GET", "/me", conta.Token);

        await ConferirProblemaAsync(resposta, HttpStatusCode.Unauthorized, "Não autenticado");
    }

    [Fact]
    public async Task Promover_e_rebaixar_valem_no_pedido_seguinte_com_o_mesmo_token()
    {
        var conta = await fabrica.Contas.CriarAsync(Papel.Usuario);

        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync("GET", "/usuarios", conta.Token)).StatusCode);

        await fabrica.Contas.DefinirPapelAsync(conta.Id, Papel.Coordenacao);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync("GET", "/usuarios", conta.Token)).StatusCode);

        await fabrica.Contas.DefinirPapelAsync(conta.Id, Papel.Usuario);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnviarAsync("GET", "/usuarios", conta.Token)).StatusCode);
    }

    [Fact]
    public async Task Coordenacao_passa_na_politica_equipe_mas_equipe_nao_passa_na_da_coordenacao()
    {
        // Confere a política direta, sem passar por uma rota (as rotas de treinamento do 1.4 a usam).
        var autorizacao = fabrica.Services.GetRequiredService<IAuthorizationService>();

        async Task<bool> Passa(Papel papel, string politica)
        {
            var identidade = new ClaimsIdentity([new Claim("role", papel.ToString())], "teste", "name", "role");
            var resultado = await autorizacao.AuthorizeAsync(new ClaimsPrincipal(identidade), null, politica);
            return resultado.Succeeded;
        }

        Assert.True(await Passa(Papel.Coordenacao, Politicas.Equipe));
        Assert.True(await Passa(Papel.Equipe, Politicas.Equipe));
        Assert.False(await Passa(Papel.Usuario, Politicas.Equipe));
        Assert.True(await Passa(Papel.Coordenacao, Politicas.Coordenacao));
        Assert.False(await Passa(Papel.Equipe, Politicas.Coordenacao));
    }

    [Fact]
    public void So_as_rotas_publicas_esperadas_aceitam_anonimo()
    {
        var rotas = fabrica.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        var anonimas = rotas
            .Where(r => r.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(r => "/" + r.RoutePattern.RawText!.TrimStart('/'))
            .ToList();

        Assert.Contains("/auth/cadastro", anonimas);
        Assert.Contains("/auth/login", anonimas);
        Assert.Contains("/saude", anonimas);
        Assert.All(anonimas, rota => Assert.True(
            rota is "/auth/cadastro" or "/auth/login" or "/saude"
                || rota.StartsWith("/openapi", StringComparison.Ordinal)
                || rota.StartsWith("/scalar", StringComparison.Ordinal),
            $"Rota anônima inesperada: {rota}"));
        // As demais rotas existem e caem na política padrão (login obrigatório).
        Assert.Contains(rotas, r => "/" + r.RoutePattern.RawText!.TrimStart('/') == "/me" && r.Metadata.GetMetadata<IAllowAnonymous>() is null);
    }

    private static string Emitir(Guid id, string chave, DateTime inicio, DateTime fim) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Emissor,
            Audience = Emissor,
            IssuedAt = inicio,
            NotBefore = inicio,
            Expires = fim,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(chave)), SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity([new Claim("sub", id.ToString())]),
        });

    private static string Base64Url(string texto) => Base64UrlEncoder.Encode(texto);
}
