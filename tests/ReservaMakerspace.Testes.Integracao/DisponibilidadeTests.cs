using System.Net;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Dominio;
using static ReservaMakerspace.Testes.Integracao.AuxiliarDeReservas;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// <c>GET /disponibilidade</c> (plano do 1.6, decisões 1, 2 e 4; itens 1 e 2 do Foco). Cada teste cria o próprio tipo,
/// porque a coleção compartilha o banco.
/// </summary>
[Collection(ApiCollection.Nome)]
public class DisponibilidadeTests(FabricaDaApi fabrica)
{
    private readonly AuxiliarDeReservas _reservas = new(fabrica.Contas);

    private static string Rota(Guid tipoId, DateTimeOffset inicio, DateTimeOffset fim) =>
        $"/disponibilidade?tipoId={tipoId}&inicio={Horario(inicio)}&fim={Horario(fim)}";

    private async Task<List<EquipamentoResposta>> ConsultarAsync(HttpClient cliente, string rota)
    {
        var resposta = await cliente.GetAsync(rota, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return await Json.LerAsync<List<EquipamentoResposta>>(resposta);
    }

    private async Task<HttpClient> ClienteDeUsuarioAsync() =>
        fabrica.Contas.ClienteCom((await fabrica.Contas.CriarAsync(Papel.Usuario)).Token);

    [Fact]
    public async Task Lista_as_maquinas_livres_do_tipo_em_ordem_de_nome()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var outroTipo = await _reservas.CriarTipoAsync();
        var equipe = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        var dono = await _reservas.PessoaTreinadaAsync(tipo.Id);

        // As máquinas nascem fora da ordem de nome, para a ordem do resultado não ser a da criação.
        var cruzando = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "D cruzando");
        var livreB = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "B encostada antes");
        var livreC = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "C encostada depois");
        var livreA = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "A sem reservas");
        var cancelada = await _reservas.CriarMaquinaAsync(tipo.Id, prefixo: "E so cancelada");
        var inativa = await _reservas.CriarMaquinaAsync(tipo.Id, ativo: false, prefixo: "F inativa");
        await _reservas.CriarMaquinaAsync(outroTipo.Id, prefixo: "G de outro tipo");

        var dia = Dia(3);
        var inicio = dia.AddHours(10);
        var fim = dia.AddHours(12);
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        using var doDono = fabrica.Contas.ClienteCom(dono.Token);
        await ReservarOkAsync(daEquipe, cruzando.Id, dia.AddHours(11), dia.AddHours(13), "DemandaDoParque");
        await ReservarOkAsync(daEquipe, livreB.Id, dia.AddHours(8), inicio, "DemandaDoParque");
        await ReservarOkAsync(daEquipe, livreC.Id, fim, dia.AddHours(14), "DemandaDoParque");
        var daCancelada = await ReservarOkAsync(doDono, cancelada.Id, inicio, fim);
        Assert.Equal(HttpStatusCode.OK, (await CancelarAsync(doDono, daCancelada.Id)).StatusCode);

        var livres = await ConsultarAsync(doDono, Rota(tipo.Id, inicio, fim));

        Assert.Equal([livreA.Id, livreB.Id, livreC.Id, cancelada.Id], livres.Select(m => m.Id));
        Assert.DoesNotContain(livres, m => m.Id == cruzando.Id || m.Id == inativa.Id);
        // O formato é o do GET /equipamentos.
        Assert.Equal(livreA, livres[0]);
    }

    [Fact]
    public async Task Sem_maquina_livre_a_lista_vem_vazia()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var equipe = await _reservas.PessoaTreinadaAsync(tipo.Id, Papel.Equipe);
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        await _reservas.CriarMaquinaAsync(tipo.Id, ativo: false);
        var dia = Dia(3);
        using var daEquipe = fabrica.Contas.ClienteCom(equipe.Token);
        await ReservarOkAsync(daEquipe, maquina.Id, dia.AddHours(9), dia.AddHours(18), "DemandaDoParque");

        using var cliente = await ClienteDeUsuarioAsync();
        var livres = await ConsultarAsync(cliente, Rota(tipo.Id, dia.AddHours(10), dia.AddHours(11)));

        Assert.Empty(livres);
    }

    [Fact]
    public async Task O_fuso_da_query_conta_e_a_janela_nao_tem_limite()
    {
        var tipo = await _reservas.CriarTipoAsync();
        var maquina = await _reservas.CriarMaquinaAsync(tipo.Id);
        var inicio = Dia(3).AddHours(13);
        var fim = inicio.AddHours(1);
        using var cliente = await ClienteDeUsuarioAsync();

        // 10:00-03:00 é 13:00Z (o "+" da URL precisa ir como %2B).
        var emUtc = await ConsultarAsync(cliente, Rota(tipo.Id, inicio, fim));
        var comOffset = await ConsultarAsync(
            cliente, $"/disponibilidade?tipoId={tipo.Id}&inicio={Uri.EscapeDataString(inicio.ToOffset(TimeSpan.FromHours(-3)).ToString("yyyy-MM-dd'T'HH:mm:sszzz"))}&fim={Uri.EscapeDataString(fim.ToOffset(TimeSpan.FromHours(5)).ToString("yyyy-MM-dd'T'HH:mm:sszzz"))}");
        var muitoLonga = await ConsultarAsync(cliente, Rota(tipo.Id, inicio, inicio.AddDays(400)));

        Assert.Equal(maquina.Id, Assert.Single(emUtc).Id);
        Assert.Equal(maquina.Id, Assert.Single(comOffset).Id);
        Assert.Equal(maquina.Id, Assert.Single(muitoLonga).Id);
    }

    [Theory]
    [InlineData("", "tipoId")]
    [InlineData("tipoId=nao-e-um-guid&inicio=2030-01-01T10:00:00Z&fim=2030-01-01T11:00:00Z", "tipoId")]
    [InlineData("tipoId={tipo}&fim=2030-01-01T11:00:00Z", "inicio")]
    [InlineData("tipoId={tipo}&inicio=2030-01-01T10:00:00Z", "fim")]
    [InlineData("tipoId={tipo}&inicio=2030-01-01T10:00:00&fim=2030-01-01T11:00:00Z", "inicio")]
    [InlineData("tipoId={tipo}&inicio=2030-01-01T10:00:00Z&fim=2030-01-01T11:00:00", "fim")]
    [InlineData("tipoId={tipo}&inicio=2030-01-01&fim=2030-01-01T11:00:00Z", "inicio")]
    [InlineData("tipoId={tipo}&inicio=2030-01-01T10:00:00%2003:00&fim=2030-01-01T11:00:00Z", "inicio")]
    [InlineData("tipoId={tipo}&inicio=amanha&fim=2030-01-01T11:00:00Z", "inicio")]
    [InlineData("tipoId={tipo}&inicio=2030-01-01T10:00:00Z&fim=2030-01-01T10:00:00Z", "fim")]
    [InlineData("tipoId={tipo}&inicio=2030-01-01T10:00:00Z&fim=2030-01-01T09:00:00Z", "fim")]
    [InlineData("tipoId=00000000-0000-0000-0000-000000000001&inicio=2030-01-01T10:00:00Z&fim=2030-01-01T11:00:00Z", "tipoId")]
    public async Task Parametro_ausente_invalido_ou_sem_fuso_responde_400_no_campo(string query, string campo)
    {
        var tipo = await _reservas.CriarTipoAsync();
        using var cliente = await ClienteDeUsuarioAsync();

        var resposta = await cliente.GetAsync(
            $"/disponibilidade?{query.Replace("{tipo}", tipo.Id.ToString())}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        using var corpo = await Json.DocumentoAsync(resposta);
        Assert.True(
            corpo.RootElement.GetProperty("errors").TryGetProperty(campo, out _),
            $"Sem erro em '{campo}': {corpo.RootElement}");
    }
}
