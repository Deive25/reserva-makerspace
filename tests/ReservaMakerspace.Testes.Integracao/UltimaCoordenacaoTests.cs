using System.Net;
using System.Net.Http.Json;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

public class UltimaCoordenacaoTests(BancosDeTeste bancos) : IClassFixture<BancosDeTeste>
{
    private const string TotalDeCoordenacoes = "SELECT count(*) FROM usuarios WHERE papel = 'Coordenacao'";

    private async Task<(FabricaSemBanco Fabrica, string ConnectionString, AuxiliarDeContas Contas, string Email, string Senha)> SubirAsync()
    {
        var connectionString = await bancos.NovoBancoAsync();
        var email = AuxiliarDeContas.NovoEmail();
        var senha = AuxiliarDeContas.NovaSenha();
        var fabrica = new FabricaSemBanco(
            ConfiguracaoDeTeste.Para(connectionString, ConfiguracaoDeTeste.NovaChave(), email, senha));
        return (fabrica, connectionString, new AuxiliarDeContas(fabrica, email, senha), email, senha);
    }

    [Fact]
    public async Task A_unica_coordenacao_nao_pode_se_rebaixar()
    {
        var (fabrica, connectionString, contas, _, _) = await SubirAsync();
        await using var liberar = fabrica;
        var coordenacao = await contas.CoordenacaoInicialAsync();
        using var cliente = contas.ClienteCom(coordenacao.Token);

        var resposta = await cliente.PutAsJsonAsync(
            $"/usuarios/{coordenacao.Id}/papel", new { papel = "Equipe" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal("A última Coordenação não pode perder o papel.", corpo.RootElement.GetProperty("detail").GetString());
        Assert.Equal(1, await BancosDeTeste.ContarAsync(connectionString, TotalDeCoordenacoes));
    }

    [Fact]
    public async Task Duas_coordenacoes_rebaixando_uma_a_outra_ao_mesmo_tempo_deixam_exatamente_uma()
    {
        var (fabrica, connectionString, contas, _, _) = await SubirAsync();
        await using var liberar = fabrica;
        var a = await contas.CoordenacaoInicialAsync();
        var b = await contas.CriarAsync(Papel.Coordenacao);
        using var clienteA = contas.ClienteCom(a.Token);
        using var clienteB = contas.ClienteCom(b.Token);

        // Várias rodadas, porque a corrida só aparece quando os dois pedidos se cruzam no banco.
        for (var rodada = 0; rodada < 8; rodada++)
        {
            Assert.Equal(2, await BancosDeTeste.ContarAsync(connectionString, TotalDeCoordenacoes));

            var rebaixaB = clienteA.PutAsJsonAsync(
                $"/usuarios/{b.Id}/papel", new { papel = "Usuario" }, TestContext.Current.CancellationToken);
            var rebaixaA = clienteB.PutAsJsonAsync(
                $"/usuarios/{a.Id}/papel", new { papel = "Usuario" }, TestContext.Current.CancellationToken);
            var respostas = await Task.WhenAll(rebaixaB, rebaixaA);

            Assert.True(
                respostas.Count(r => r.StatusCode == HttpStatusCode.OK) <= 1,
                $"Rodada {rodada}: os dois rebaixamentos deram 200.");
            Assert.Equal(1, await BancosDeTeste.ContarAsync(connectionString, TotalDeCoordenacoes));

            // Quem sobrou devolve o papel à outra pessoa para a próxima rodada.
            var sobrou = await BancosDeTeste.ContarAsync(
                connectionString, $"SELECT count(*) FROM usuarios WHERE papel = 'Coordenacao' AND id = '{a.Id}'") == 1
                ? (Quem: clienteA, Outra: b.Id)
                : (Quem: clienteB, Outra: a.Id);
            var promocao = await sobrou.Quem.PutAsJsonAsync(
                $"/usuarios/{sobrou.Outra}/papel", new { papel = "Coordenacao" }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, promocao.StatusCode);
        }
    }
}
