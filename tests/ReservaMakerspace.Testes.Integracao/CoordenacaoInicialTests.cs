using System.Net;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

public class CoordenacaoInicialTests(BancosDeTeste bancos) : IClassFixture<BancosDeTeste>
{
    private const string TotalDeCoordenacoes = "SELECT count(*) FROM usuarios WHERE papel = 'Coordenacao'";
    private const string TotalDeUsuarios = "SELECT count(*) FROM usuarios";

    private static FabricaSemBanco Fabrica(string connectionString, string? email = null, string? senha = null) =>
        new(ConfiguracaoDeTeste.Para(connectionString, ConfiguracaoDeTeste.NovaChave(), email, senha));

    [Fact]
    public async Task Sem_as_variaveis_a_api_sobe_sem_coordenacao()
    {
        var connectionString = await bancos.NovoBancoAsync();
        await using var fabrica = Fabrica(connectionString);

        using var cliente = fabrica.CreateClient();

        Assert.Equal(0, await BancosDeTeste.ContarAsync(connectionString, TotalDeCoordenacoes));
    }

    [Fact]
    public async Task Com_as_variaveis_cria_a_coordenacao_e_ela_consegue_entrar()
    {
        var connectionString = await bancos.NovoBancoAsync();
        var email = AuxiliarDeContas.NovoEmail();
        var senha = AuxiliarDeContas.NovaSenha();
        await using var fabrica = Fabrica(connectionString, email, senha);
        var contas = new AuxiliarDeContas(fabrica, email, senha);

        var coordenacao = await contas.CoordenacaoInicialAsync();

        Assert.Equal(1, await BancosDeTeste.ContarAsync(connectionString, TotalDeCoordenacoes));
        using var cliente = contas.ClienteCom(coordenacao.Token);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/usuarios", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Ao_subir_de_novo_nao_duplica()
    {
        var connectionString = await bancos.NovoBancoAsync();
        var email = AuxiliarDeContas.NovoEmail();
        var senha = AuxiliarDeContas.NovaSenha();
        await using (var primeira = Fabrica(connectionString, email, senha))
        {
            using var cliente = primeira.CreateClient();
        }

        await using var segunda = Fabrica(connectionString, email, senha);
        using var outroCliente = segunda.CreateClient();

        Assert.Equal(1, await BancosDeTeste.ContarAsync(connectionString, TotalDeCoordenacoes));
        Assert.Equal(1, await BancosDeTeste.ContarAsync(connectionString, TotalDeUsuarios));
    }

    [Fact]
    public async Task Com_o_email_ja_cadastrado_nao_promove_e_a_subida_falha()
    {
        var connectionString = await bancos.NovoBancoAsync();
        var email = AuxiliarDeContas.NovoEmail();
        await using (var semCoordenacao = Fabrica(connectionString))
        {
            await new AuxiliarDeContas(semCoordenacao, email, "").CadastrarAsync(email, AuxiliarDeContas.NovaSenha());
        }

        await using var fabrica = Fabrica(connectionString, email, AuxiliarDeContas.NovaSenha());
        var erro = Assert.Throws<InvalidOperationException>(() => fabrica.CreateClient());

        Assert.Contains("O e-mail de CoordenacaoInicial já tem conta. Use outro e-mail.", erro.Message);
        Assert.Equal(0, await BancosDeTeste.ContarAsync(connectionString, TotalDeCoordenacoes));
        Assert.Equal(1, await BancosDeTeste.ContarAsync(connectionString, TotalDeUsuarios));
    }

    [Fact]
    public async Task Com_senha_fora_da_politica_a_subida_falha_com_a_mensagem_do_identity()
    {
        var connectionString = await bancos.NovoBancoAsync();
        await using var fabrica = Fabrica(connectionString, AuxiliarDeContas.NovoEmail(), "curta");

        var erro = Assert.Throws<InvalidOperationException>(() => fabrica.CreateClient());

        Assert.Contains("Coordenação inicial", erro.Message);
        Assert.Equal(0, await BancosDeTeste.ContarAsync(connectionString, TotalDeUsuarios));
    }
}
