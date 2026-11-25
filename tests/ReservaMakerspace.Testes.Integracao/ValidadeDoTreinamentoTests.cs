using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Contratos.Treinamentos;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// A validade de 12 meses e a revogação com o relógio avançando de verdade (<see cref="FakeTimeProvider"/>).
/// Cada teste sobe a API num banco novo, com o relógio parado num instante conhecido.
/// </summary>
public class ValidadeDoTreinamentoTests(BancosDeTeste bancos) : IClassFixture<BancosDeTeste>
{
    private static readonly DateTimeOffset Inicio = new(2027, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed record Cenario(
        FabricaComRelogio Fabrica,
        ContaDeTeste Equipe,
        ContaDeTeste Pessoa,
        TipoDeEquipamentoResposta Tipo);

    private async Task<FabricaComRelogio> NovaFabricaAsync() =>
        new(await bancos.NovoBancoAsync(), new FakeTimeProvider(Inicio));

    private static async Task<Cenario> MontarAsync(FabricaComRelogio fabrica)
    {
        var coordenacao = await fabrica.Contas.CoordenacaoInicialAsync();
        using var daCoordenacao = fabrica.Contas.ClienteCom(coordenacao.Token);
        var resposta = await daCoordenacao.PostAsJsonAsync(
            "/tipos-de-equipamento",
            new { nome = "Impressora 3D FDM", duracaoMaximaPessoalMinutos = 120 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var tipo = await Json.LerAsync<TipoDeEquipamentoResposta>(resposta);
        var equipe = await fabrica.Contas.CriarAsync(Papel.Equipe);
        var pessoa = await fabrica.Contas.CriarAsync(Papel.Usuario);
        return new Cenario(fabrica, equipe, pessoa, tipo);
    }

    /// <summary>Os tokens duram 60 minutos: depois de avançar o relógio, cada pessoa entra de novo.</summary>
    private static async Task<HttpClient> ClienteAsync(Cenario c, ContaDeTeste conta) =>
        c.Fabrica.Contas.ClienteCom(await c.Fabrica.Contas.EntrarAsync(conta.Email, conta.Senha));

    private static async Task<TreinamentoResposta> RegistrarAsync(Cenario c)
    {
        using var cliente = await ClienteAsync(c, c.Equipe);
        var resposta = await cliente.PostAsJsonAsync(
            "/treinamentos", new { usuarioId = c.Pessoa.Id, tipoId = c.Tipo.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return await Json.LerAsync<TreinamentoResposta>(resposta);
    }

    private static async Task<TreinamentoResposta> BuscarAsync(Cenario c, Guid id)
    {
        using var cliente = await ClienteAsync(c, c.Equipe);
        var resposta = await cliente.GetAsync($"/treinamentos/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return await Json.LerAsync<TreinamentoResposta>(resposta);
    }

    private static async Task<HttpResponseMessage> RevogarAsync(Cenario c, Guid id)
    {
        using var cliente = await ClienteAsync(c, c.Equipe);
        return await cliente.PostAsJsonAsync(
            $"/treinamentos/{id}/revogacao", new { motivo = "Motivo da revogação." }, TestContext.Current.CancellationToken);
    }

    private static async Task<List<TreinamentoResposta>> MeusAsync(Cenario c)
    {
        using var cliente = await ClienteAsync(c, c.Pessoa);
        return await Json.LerAsync<List<TreinamentoResposta>>(
            await cliente.GetAsync("/me/treinamentos", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Registrar_grava_a_data_do_relogio_da_api_e_a_validade_de_12_meses()
    {
        await using var fabrica = await NovaFabricaAsync();
        var c = await MontarAsync(fabrica);

        var criado = await RegistrarAsync(c);

        Assert.Equal(Inicio, criado.RealizadoEm);
        Assert.Equal(Inicio.AddMonths(12), criado.ValidoAte);
        Assert.Equal(SituacaoDoTreinamento.Valido, criado.Situacao);
    }

    [Fact]
    public async Task Um_segundo_antes_dos_12_meses_e_valido_e_no_instante_exato_esta_vencido()
    {
        await using var fabrica = await NovaFabricaAsync();
        var c = await MontarAsync(fabrica);
        var criado = await RegistrarAsync(c);

        fabrica.Relogio.SetUtcNow(criado.ValidoAte - TimeSpan.FromSeconds(1));
        Assert.Equal(SituacaoDoTreinamento.Valido, (await BuscarAsync(c, criado.Id)).Situacao);
        Assert.Equal(SituacaoDoTreinamento.Valido, Assert.Single(await MeusAsync(c)).Situacao);

        fabrica.Relogio.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SituacaoDoTreinamento.Vencido, (await BuscarAsync(c, criado.Id)).Situacao);
        Assert.Equal(SituacaoDoTreinamento.Vencido, Assert.Single(await MeusAsync(c)).Situacao);
    }

    [Fact]
    public async Task Revogar_um_treinamento_vencido_da_409_e_ele_continua_vencido()
    {
        await using var fabrica = await NovaFabricaAsync();
        var c = await MontarAsync(fabrica);
        var criado = await RegistrarAsync(c);
        fabrica.Relogio.SetUtcNow(criado.ValidoAte);

        var resposta = await RevogarAsync(c, criado.Id);

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.Equal("O treinamento já venceu.", corpo.RootElement.GetProperty("detail").GetString());
        var atual = await BuscarAsync(c, criado.Id);
        Assert.Equal(SituacaoDoTreinamento.Vencido, atual.Situacao);
        Assert.Null(atual.RevogadoEm);
    }

    [Fact]
    public async Task Renovar_e_registrar_de_novo_o_novo_e_valido_o_anterior_vencido_e_os_dois_ficam_no_historico()
    {
        await using var fabrica = await NovaFabricaAsync();
        var c = await MontarAsync(fabrica);
        var primeiro = await RegistrarAsync(c);

        fabrica.Relogio.Advance(TimeSpan.FromDays(200));
        var renovado = await RegistrarAsync(c);
        // 13 meses depois do primeiro registro e 7 do segundo.
        fabrica.Relogio.SetUtcNow(primeiro.ValidoAte.AddDays(30));

        var meus = await MeusAsync(c);
        Assert.Equal([renovado.Id, primeiro.Id], meus.Select(t => t.Id));
        Assert.Equal(SituacaoDoTreinamento.Valido, meus[0].Situacao);
        Assert.Equal(SituacaoDoTreinamento.Vencido, meus[1].Situacao);
        Assert.Equal(primeiro.ValidoAte, meus[1].ValidoAte);
    }

    [Fact]
    public async Task Revogar_o_renovado_nao_transforma_o_registro_vencido_em_revogado_e_a_revogacao_prevalece_depois()
    {
        await using var fabrica = await NovaFabricaAsync();
        var c = await MontarAsync(fabrica);
        var primeiro = await RegistrarAsync(c);
        fabrica.Relogio.Advance(TimeSpan.FromDays(200));
        var renovado = await RegistrarAsync(c);
        fabrica.Relogio.SetUtcNow(primeiro.ValidoAte.AddDays(30));

        var resposta = await RevogarAsync(c, renovado.Id);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(SituacaoDoTreinamento.Vencido, (await BuscarAsync(c, primeiro.Id)).Situacao);
        Assert.Equal(SituacaoDoTreinamento.Revogado, (await BuscarAsync(c, renovado.Id)).Situacao);

        // Passado o prazo do renovado, a revogação continua prevalecendo sobre o vencimento.
        fabrica.Relogio.SetUtcNow(renovado.ValidoAte.AddDays(1));
        Assert.Equal(SituacaoDoTreinamento.Revogado, (await BuscarAsync(c, renovado.Id)).Situacao);
        Assert.Equal(SituacaoDoTreinamento.Vencido, (await BuscarAsync(c, primeiro.Id)).Situacao);
    }

    [Fact]
    public async Task Revogar_pela_pessoa_e_tipo_revoga_o_anterior_ainda_valido_com_o_mesmo_instante_e_motivo()
    {
        await using var fabrica = await NovaFabricaAsync();
        var c = await MontarAsync(fabrica);
        var primeiro = await RegistrarAsync(c);
        fabrica.Relogio.Advance(TimeSpan.FromDays(100));
        var renovado = await RegistrarAsync(c);
        fabrica.Relogio.Advance(TimeSpan.FromDays(10));

        var resposta = await RevogarAsync(c, renovado.Id);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var anterior = await BuscarAsync(c, primeiro.Id);
        Assert.Equal(SituacaoDoTreinamento.Revogado, anterior.Situacao);
        Assert.Equal("Motivo da revogação.", anterior.MotivoDaRevogacao);
        Assert.Equal(fabrica.Relogio.GetUtcNow(), anterior.RevogadoEm);
    }

    [Fact]
    public async Task Token_emitido_antes_de_o_relogio_avancar_61_minutos_deixa_de_valer_e_um_login_novo_funciona()
    {
        await using var fabrica = await NovaFabricaAsync();
        var conta = await fabrica.Contas.CriarAsync(Papel.Usuario);

        using (var cliente = fabrica.Contas.ClienteCom(conta.Token))
        {
            fabrica.Relogio.Advance(TimeSpan.FromMinutes(30));
            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/me", TestContext.Current.CancellationToken)).StatusCode);

            fabrica.Relogio.Advance(TimeSpan.FromMinutes(31));
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await cliente.GetAsync("/me", TestContext.Current.CancellationToken)).StatusCode);
        }

        var novoToken = await fabrica.Contas.EntrarAsync(conta.Email, conta.Senha);
        using var novoCliente = fabrica.Contas.ClienteCom(novoToken);
        Assert.Equal(HttpStatusCode.OK, (await novoCliente.GetAsync("/me", TestContext.Current.CancellationToken)).StatusCode);
    }
}
