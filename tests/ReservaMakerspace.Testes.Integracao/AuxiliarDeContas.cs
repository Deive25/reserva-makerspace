using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using ReservaMakerspace.Contratos.Contas;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

public sealed record ContaDeTeste(Guid Id, string Email, string Senha, string Token);

public static class Json
{
    public static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<T> LerAsync<T>(HttpResponseMessage resposta) =>
        (await resposta.Content.ReadFromJsonAsync<T>(Opcoes, TestContext.Current.CancellationToken))!;

    public static async Task<JsonDocument> DocumentoAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
}

/// <summary>
/// Cadastra, promove e faz login de pessoas de teste. Cada pessoa tem e-mail único (Guid),
/// porque os testes da coleção compartilham o banco.
/// </summary>
public sealed class AuxiliarDeContas(
    WebApplicationFactory<Program> fabrica,
    string coordenacaoEmail,
    string coordenacaoSenha)
{
    private string? _tokenDaCoordenacao;

    public static string NovoEmail() => $"{Guid.NewGuid():N}@teste.example";

    public static string NovaSenha() => $"Senha-{Guid.NewGuid():N}";

    public HttpClient ClienteCom(string? token = null)
    {
        var cliente = fabrica.CreateClient();
        if (token is not null)
        {
            cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return cliente;
    }

    public async Task<UsuarioResposta> CadastrarAsync(string email, string senha, string nome = "Pessoa de Teste")
    {
        using var cliente = ClienteCom();
        var resposta = await cliente.PostAsJsonAsync(
            "/auth/cadastro", new { nome, email, senha }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Created, resposta.StatusCode);
        return await Json.LerAsync<UsuarioResposta>(resposta);
    }

    public async Task<string> EntrarAsync(string email, string senha)
    {
        using var cliente = ClienteCom();
        var resposta = await cliente.PostAsJsonAsync(
            "/auth/login", new { email, senha }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, resposta.StatusCode);
        return (await Json.LerAsync<RespostaDeLogin>(resposta)).Token;
    }

    /// <summary>Cadastra uma pessoa nova, promove ao papel pedido (pela Coordenação inicial) e faz login.</summary>
    public async Task<ContaDeTeste> CriarAsync(Papel papel)
    {
        var email = NovoEmail();
        var senha = NovaSenha();
        var usuario = await CadastrarAsync(email, senha);
        if (papel != Papel.Usuario)
        {
            await DefinirPapelAsync(usuario.Id, papel);
        }

        return new ContaDeTeste(usuario.Id, email, senha, await EntrarAsync(email, senha));
    }

    public async Task<ContaDeTeste> CoordenacaoInicialAsync()
    {
        _tokenDaCoordenacao ??= await EntrarAsync(coordenacaoEmail, coordenacaoSenha);
        using var cliente = ClienteCom(_tokenDaCoordenacao);
        var eu = await Json.LerAsync<UsuarioResposta>(
            await cliente.GetAsync("/me", TestContext.Current.CancellationToken));
        return new ContaDeTeste(eu.Id, coordenacaoEmail, coordenacaoSenha, _tokenDaCoordenacao);
    }

    /// <summary>Define o papel com a Coordenação inicial. Devolve a resposta para quem quiser conferir.</summary>
    public async Task<HttpResponseMessage> DefinirPapelAsync(Guid id, Papel papel)
    {
        var coordenacao = await CoordenacaoInicialAsync();
        using var cliente = ClienteCom(coordenacao.Token);
        var resposta = await cliente.PutAsJsonAsync(
            $"/usuarios/{id}/papel", new { papel = papel.ToString() }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, resposta.StatusCode);
        return resposta;
    }
}
