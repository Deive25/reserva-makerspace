using System.Net;
using System.Net.Http.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using ReservaMakerspace.Contratos.Contas;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

[Collection(ApiCollection.Nome)]
public class ContasTests(FabricaDaApi fabrica)
{
    public static TheoryData<string, HttpStatusCode> Senhas => new()
    {
        // 14 caracteres: um a menos que o mínimo.
        { "pão com café 1", HttpStatusCode.BadRequest },
        // 15 caracteres, com espaços e acentos: sem regras de composição, só o tamanho.
        { "pão com café 12", HttpStatusCode.Created },
    };

    [Fact]
    public async Task Cadastro_cria_usuario_com_papel_usuario()
    {
        using var cliente = fabrica.CreateClient();
        var email = AuxiliarDeContas.NovoEmail();

        var resposta = await cliente.PostAsJsonAsync(
            "/auth/cadastro",
            new { nome = "Ana", email, senha = AuxiliarDeContas.NovaSenha() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal("/me", resposta.Headers.Location?.OriginalString);
        var usuario = await Json.LerAsync<UsuarioResposta>(resposta);
        Assert.Equal("Ana", usuario.Nome);
        Assert.Equal(email, usuario.Email);
        Assert.Equal(Papel.Usuario, usuario.Papel);
    }

    [Theory]
    [MemberData(nameof(Senhas))]
    public async Task Senha_tem_minimo_de_15_caracteres_sem_regras_de_composicao(string senha, HttpStatusCode esperado)
    {
        using var cliente = fabrica.CreateClient();

        var resposta = await cliente.PostAsJsonAsync(
            "/auth/cadastro",
            new { nome = "Ana", email = AuxiliarDeContas.NovoEmail(), senha },
            TestContext.Current.CancellationToken);

        Assert.Equal(esperado, resposta.StatusCode);
        if (esperado == HttpStatusCode.BadRequest)
        {
            using var corpo = await Json.DocumentoAsync(resposta);
            var erros = corpo.RootElement.GetProperty("errors").GetProperty("senha");
            Assert.Equal("A senha precisa ter pelo menos 15 caracteres.", erros[0].GetString());
        }
    }

    [Fact]
    public async Task Email_invalido_responde_400_em_errors_email()
    {
        using var cliente = fabrica.CreateClient();

        var resposta = await cliente.PostAsJsonAsync(
            "/auth/cadastro",
            new { nome = "Ana", email = "isto-nao-e-email", senha = AuxiliarDeContas.NovaSenha() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.True(corpo.RootElement.GetProperty("errors").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Email_repetido_com_outra_caixa_responde_409_e_o_login_acha_a_mesma_conta()
    {
        using var cliente = fabrica.CreateClient();
        var sufixo = Guid.NewGuid().ToString("N");
        var senha = AuxiliarDeContas.NovaSenha();
        var primeira = await fabrica.Contas.CadastrarAsync($"Ana.{sufixo}@Teste.example", senha);

        var repetida = await cliente.PostAsJsonAsync(
            "/auth/cadastro",
            new { nome = "Outra Ana", email = $"ana.{sufixo}@teste.EXAMPLE", senha = AuxiliarDeContas.NovaSenha() },
            TestContext.Current.CancellationToken);
        var login = await cliente.PostAsJsonAsync(
            "/auth/login",
            new { email = $"ANA.{sufixo}@teste.example", senha },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, repetida.StatusCode);
        using var corpo = await Json.DocumentoAsync(repetida);
        Assert.Equal("Conflito", corpo.RootElement.GetProperty("title").GetString());
        Assert.Equal("Já existe uma conta com este e-mail.", corpo.RootElement.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(primeira.Id, (await Json.LerAsync<RespostaDeLogin>(login)).Usuario.Id);
    }

    [Fact]
    public async Task Cadastro_com_papel_coordenacao_no_corpo_cria_usuario()
    {
        // Mass assignment: o pedido não tem campo de papel, então o JSON extra é ignorado.
        using var cliente = fabrica.CreateClient();

        var resposta = await cliente.PostAsJsonAsync(
            "/auth/cadastro",
            new { nome = "Intrusa", email = AuxiliarDeContas.NovoEmail(), senha = AuxiliarDeContas.NovaSenha(), papel = "Coordenacao" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal(Papel.Usuario, (await Json.LerAsync<UsuarioResposta>(resposta)).Papel);
    }

    [Fact]
    public async Task Login_devolve_jwt_hs256_de_60_minutos_com_sub_igual_ao_id()
    {
        using var cliente = fabrica.CreateClient();
        var email = AuxiliarDeContas.NovoEmail();
        var senha = AuxiliarDeContas.NovaSenha();
        var usuario = await fabrica.Contas.CadastrarAsync(email, senha);

        var resposta = await cliente.PostAsJsonAsync(
            "/auth/login", new { email, senha }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var login = await Json.LerAsync<RespostaDeLogin>(resposta);
        var token = new JsonWebTokenHandler().ReadJsonWebToken(login.Token);
        Assert.Equal("HS256", token.Alg);
        Assert.Equal(3600, new DateTimeOffset(token.ValidTo).ToUnixTimeSeconds() - new DateTimeOffset(token.IssuedAt).ToUnixTimeSeconds());
        Assert.Equal(login.ExpiraEm.ToUnixTimeSeconds(), new DateTimeOffset(token.ValidTo).ToUnixTimeSeconds());
        Assert.Equal(usuario.Id.ToString(), token.Subject);
        Assert.Equal(email, token.GetClaim("email").Value);
        // O papel não vai no token: é lido do banco a cada pedido.
        Assert.False(token.TryGetClaim("role", out _));
        Assert.Equal(usuario, login.Usuario);
    }

    [Fact]
    public async Task Login_com_email_inexistente_e_com_senha_errada_dao_o_mesmo_401()
    {
        using var cliente = fabrica.CreateClient();
        var conta = await fabrica.Contas.CriarAsync(Papel.Usuario);

        var inexistente = await cliente.PostAsJsonAsync(
            "/auth/login",
            new { email = AuxiliarDeContas.NovoEmail(), senha = AuxiliarDeContas.NovaSenha() },
            TestContext.Current.CancellationToken);
        var senhaErrada = await cliente.PostAsJsonAsync(
            "/auth/login",
            new { email = conta.Email, senha = AuxiliarDeContas.NovaSenha() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, inexistente.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, senhaErrada.StatusCode);
        using var corpoA = await Json.DocumentoAsync(inexistente);
        using var corpoB = await Json.DocumentoAsync(senhaErrada);
        Assert.Equal("E-mail ou senha incorretos.", corpoA.RootElement.GetProperty("detail").GetString());
        Assert.Equal(
            corpoA.RootElement.GetProperty("detail").GetString(),
            corpoB.RootElement.GetProperty("detail").GetString());
        Assert.Equal(
            corpoA.RootElement.GetProperty("title").GetString(),
            corpoB.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Login_sem_campos_responde_400()
    {
        using var cliente = fabrica.CreateClient();

        var resposta = await cliente.PostAsJsonAsync(
            "/auth/login", new { }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Me_devolve_o_dono_do_token()
    {
        var conta = await fabrica.Contas.CriarAsync(Papel.Equipe);
        using var cliente = fabrica.Contas.ClienteCom(conta.Token);

        var resposta = await cliente.GetAsync("/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var eu = await Json.LerAsync<UsuarioResposta>(resposta);
        Assert.Equal(conta.Id, eu.Id);
        Assert.Equal(conta.Email, eu.Email);
        Assert.Equal(Papel.Equipe, eu.Papel);
    }

    [Fact]
    public async Task Nenhuma_resposta_traz_campos_internos_do_identity()
    {
        using var cliente = fabrica.CreateClient();
        var email = AuxiliarDeContas.NovoEmail();
        var senha = AuxiliarDeContas.NovaSenha();
        var coordenacao = await fabrica.Contas.CoordenacaoInicialAsync();
        using var autenticado = fabrica.Contas.ClienteCom(coordenacao.Token);

        var cadastro = await cliente.PostAsJsonAsync(
            "/auth/cadastro", new { nome = "Ana", email, senha }, TestContext.Current.CancellationToken);
        var login = await cliente.PostAsJsonAsync(
            "/auth/login", new { email, senha }, TestContext.Current.CancellationToken);
        var me = await autenticado.GetAsync("/me", TestContext.Current.CancellationToken);
        var lista = await autenticado.GetAsync("/usuarios", TestContext.Current.CancellationToken);
        var papel = await autenticado.PutAsJsonAsync(
            $"/usuarios/{(await Json.LerAsync<UsuarioResposta>(cadastro)).Id}/papel",
            new { papel = "Equipe" },
            TestContext.Current.CancellationToken);

        foreach (var resposta in new[] { cadastro, login, me, lista, papel })
        {
            var texto = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("passwordHash", texto, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("securityStamp", texto, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("concurrencyStamp", texto, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("normalized", texto, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(senha, texto, StringComparison.Ordinal);
        }
    }
}
