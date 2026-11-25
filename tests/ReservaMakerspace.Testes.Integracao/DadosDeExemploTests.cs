using Npgsql;
using ReservaMakerspace.Contratos.Treinamentos;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Integracao;

public class DadosDeExemploTests(BancosDeTeste bancos) : IClassFixture<BancosDeTeste>
{
    private const string TotalDeTipos = "SELECT count(*) FROM tipos_de_equipamento";
    private const string TotalDeMaquinas = "SELECT count(*) FROM equipamentos";
    private const string TotalDeInativas = "SELECT count(*) FROM equipamentos WHERE NOT ativo";
    private const string TotalDeUsuarios = "SELECT count(*) FROM usuarios";
    private const string TotalDeTreinamentos = "SELECT count(*) FROM treinamentos";

    private const string EmailDaAna = "aluna@makerspace.example";
    private const string EmailDoBeto = "bolsista@makerspace.example";
    private const string EmailDaCarla = "coordenadora@makerspace.example";

    private static FabricaSemBanco Fabrica(string connectionString, bool aplicar = true, string? senha = null) =>
        new(ConfiguracaoDeTeste.Para(
            connectionString,
            ConfiguracaoDeTeste.NovaChave(),
            aplicarDadosDeExemplo: aplicar,
            senhaDeExemplo: senha));

    private static Task<long> ContarAsync(string connectionString, string consulta) =>
        BancosDeTeste.ContarAsync(connectionString, consulta);

    private static async Task ExecutarAsync(string connectionString, string comando)
    {
        await using var fonte = NpgsqlDataSource.Create(connectionString);
        await using var cmd = fonte.CreateCommand(comando);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Com_a_opcao_e_a_senha_cria_tipos_maquinas_e_as_tres_pessoas_que_conseguem_entrar()
    {
        var connectionString = await bancos.NovoBancoAsync();
        var senha = AuxiliarDeContas.NovaSenha();
        await using var fabrica = Fabrica(connectionString, senha: senha);
        var contas = new AuxiliarDeContas(fabrica, "", "");

        using var cliente = fabrica.CreateClient();

        Assert.Equal(4, await ContarAsync(connectionString, TotalDeTipos));
        Assert.Equal(6, await ContarAsync(connectionString, TotalDeMaquinas));
        Assert.Equal(1, await ContarAsync(connectionString, TotalDeInativas));
        Assert.Equal(
            1,
            await ContarAsync(
                connectionString,
                "SELECT count(*) FROM equipamentos WHERE NOT ativo AND nome = 'Scanner 3D'"));
        Assert.Equal(3, await ContarAsync(connectionString, TotalDeUsuarios));
        Assert.Equal(1, await ContarAsync(connectionString, PessoaComPapel(EmailDaAna, "Usuario")));
        Assert.Equal(1, await ContarAsync(connectionString, PessoaComPapel(EmailDoBeto, "Equipe")));
        Assert.Equal(1, await ContarAsync(connectionString, PessoaComPapel(EmailDaCarla, "Coordenacao")));
        foreach (var email in new[] { EmailDaAna, EmailDoBeto, EmailDaCarla })
        {
            Assert.NotEmpty(await contas.EntrarAsync(email, senha));
        }
    }

    [Fact]
    public async Task Os_tipos_e_as_maquinas_tem_os_nomes_e_as_duracoes_do_plano()
    {
        var connectionString = await bancos.NovoBancoAsync();
        await using var fabrica = Fabrica(connectionString, senha: AuxiliarDeContas.NovaSenha());

        using var cliente = fabrica.CreateClient();

        Assert.Equal(
            1,
            await ContarAsync(
                connectionString,
                """
                SELECT count(*) FROM (
                    SELECT 1 FROM tipos_de_equipamento WHERE
                        (nome = 'Cortadora a laser' AND duracao_maxima_pessoal = interval '120 minutes') OR
                        (nome = 'Impressora 3D FDM' AND duracao_maxima_pessoal = interval '720 minutes') OR
                        (nome = 'Impressora 3D de resina' AND duracao_maxima_pessoal = interval '480 minutes') OR
                        (nome = 'Scanner 3D' AND duracao_maxima_pessoal = interval '120 minutes')
                    HAVING count(*) = 4
                ) t
                """));
        Assert.Equal(
            1,
            await ContarAsync(
                connectionString,
                """
                SELECT count(*) FROM (
                    SELECT 1 FROM equipamentos e JOIN tipos_de_equipamento t ON t.id = e.tipo_id WHERE
                        (e.nome = 'Cortadora a laser' AND t.nome = 'Cortadora a laser') OR
                        (e.nome IN ('Impressora FDM 1', 'Impressora FDM 2', 'Impressora FDM 3') AND t.nome = 'Impressora 3D FDM') OR
                        (e.nome = 'Impressora de resina' AND t.nome = 'Impressora 3D de resina') OR
                        (e.nome = 'Scanner 3D' AND t.nome = 'Scanner 3D')
                    HAVING count(*) = 6
                ) t
                """));
    }

    [Fact]
    public async Task Ao_subir_de_novo_nao_duplica_nao_devolve_o_nome_antigo_e_nao_desfaz_o_rebaixamento()
    {
        var connectionString = await bancos.NovoBancoAsync();
        var senha = AuxiliarDeContas.NovaSenha();
        await using (var primeira = Fabrica(connectionString, senha: senha))
        {
            using var cliente = primeira.CreateClient();
        }

        await ExecutarAsync(connectionString, "UPDATE equipamentos SET nome = 'Laser renomeada' WHERE nome = 'Cortadora a laser' AND tipo_id IN (SELECT id FROM tipos_de_equipamento WHERE nome = 'Cortadora a laser')");
        await ExecutarAsync(connectionString, $"UPDATE usuarios SET papel = 'Usuario' WHERE email = '{EmailDaCarla}'");

        await using var segunda = Fabrica(connectionString, senha: senha);
        using var outroCliente = segunda.CreateClient();

        Assert.Equal(4, await ContarAsync(connectionString, TotalDeTipos));
        Assert.Equal(6, await ContarAsync(connectionString, TotalDeMaquinas));
        Assert.Equal(3, await ContarAsync(connectionString, TotalDeUsuarios));
        Assert.Equal(1, await ContarAsync(connectionString, "SELECT count(*) FROM equipamentos WHERE nome = 'Laser renomeada'"));
        Assert.Equal(0, await ContarAsync(connectionString, "SELECT count(*) FROM equipamentos WHERE nome = 'Cortadora a laser'"));
        Assert.Equal(1, await ContarAsync(connectionString, PessoaComPapel(EmailDaCarla, "Usuario")));
    }

    [Fact]
    public async Task Sem_a_senha_os_tipos_e_as_maquinas_entram_e_as_pessoas_nao()
    {
        var connectionString = await bancos.NovoBancoAsync();
        await using var fabrica = Fabrica(connectionString, senha: null);

        using var cliente = fabrica.CreateClient();

        Assert.Equal(4, await ContarAsync(connectionString, TotalDeTipos));
        Assert.Equal(6, await ContarAsync(connectionString, TotalDeMaquinas));
        Assert.Equal(0, await ContarAsync(connectionString, TotalDeUsuarios));
    }

    [Fact]
    public async Task Com_senha_de_14_caracteres_a_subida_falha_com_a_mensagem_da_politica()
    {
        var connectionString = await bancos.NovoBancoAsync();
        await using var fabrica = Fabrica(connectionString, senha: new string('a', 14));

        var erro = Assert.Throws<InvalidOperationException>(() => fabrica.CreateClient());

        Assert.Contains("pessoa de exemplo", erro.Message);
        Assert.Contains("15", erro.Message);
        Assert.Equal(0, await ContarAsync(connectionString, TotalDeUsuarios));
    }

    [Fact]
    public async Task Pessoa_de_exemplo_que_ja_existe_nao_e_alterada()
    {
        var connectionString = await bancos.NovoBancoAsync();
        var senhaDela = AuxiliarDeContas.NovaSenha();
        await using (var semExemplo = Fabrica(connectionString, aplicar: false))
        {
            await new AuxiliarDeContas(semExemplo, "", "").CadastrarAsync(EmailDaAna, senhaDela);
        }

        var senhaDoExemplo = AuxiliarDeContas.NovaSenha();
        await using var fabrica = Fabrica(connectionString, senha: senhaDoExemplo);
        using var cliente = fabrica.CreateClient();
        var contas = new AuxiliarDeContas(fabrica, "", "");

        Assert.Equal(3, await ContarAsync(connectionString, TotalDeUsuarios));
        Assert.NotEmpty(await contas.EntrarAsync(EmailDaAna, senhaDela));
    }

    [Fact]
    public async Task Sem_a_opcao_o_banco_fica_sem_tipos_e_sem_as_pessoas_de_exemplo()
    {
        var connectionString = await bancos.NovoBancoAsync();
        await using var fabrica = Fabrica(connectionString, aplicar: false, senha: AuxiliarDeContas.NovaSenha());

        using var cliente = fabrica.CreateClient();

        Assert.Equal(0, await ContarAsync(connectionString, TotalDeTipos));
        Assert.Equal(0, await ContarAsync(connectionString, TotalDeMaquinas));
        Assert.Equal(0, await ContarAsync(connectionString, TotalDeUsuarios));
    }

    private static async Task<List<TreinamentoResposta>> MeusTreinamentosAsync(
        FabricaSemBanco fabrica, string email, string senha)
    {
        var contas = new AuxiliarDeContas(fabrica, "", "");
        using var cliente = contas.ClienteCom(await contas.EntrarAsync(email, senha));
        var resposta = await cliente.GetAsync("/me/treinamentos", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, resposta.StatusCode);
        return await Json.LerAsync<List<TreinamentoResposta>>(resposta);
    }

    [Fact]
    public async Task Os_treinamentos_de_exemplo_tem_as_tres_situacoes_e_a_Ana_ve_os_tres_dela_no_login()
    {
        var connectionString = await bancos.NovoBancoAsync();
        var senha = AuxiliarDeContas.NovaSenha();
        await using var fabrica = Fabrica(connectionString, senha: senha);
        using var cliente = fabrica.CreateClient();

        Assert.Equal(9, await ContarAsync(connectionString, TotalDeTreinamentos));

        var daAna = await MeusTreinamentosAsync(fabrica, EmailDaAna, senha);
        Assert.Equal(3, daAna.Count);
        var valido = Assert.Single(daAna, t => t.TipoNome == "Impressora 3D FDM");
        var vencido = Assert.Single(daAna, t => t.TipoNome == "Cortadora a laser");
        var revogado = Assert.Single(daAna, t => t.TipoNome == "Impressora 3D de resina");
        Assert.Equal(SituacaoDoTreinamento.Valido, valido.Situacao);
        Assert.Equal(SituacaoDoTreinamento.Vencido, vencido.Situacao);
        Assert.Equal(SituacaoDoTreinamento.Revogado, revogado.Situacao);
        Assert.Equal("Descarte de resina fora do recipiente.", revogado.MotivoDaRevogacao);
        Assert.Equal("Beto Bolsista", revogado.RevogadoPorNome);
        Assert.Equal("Beto Bolsista", valido.RegistradoPorNome);

        var doBeto = await MeusTreinamentosAsync(fabrica, EmailDoBeto, senha);
        Assert.Equal(5, doBeto.Count);
        Assert.Equal(4, doBeto.Count(t => t.Situacao == SituacaoDoTreinamento.Valido));
        var antigo = Assert.Single(doBeto, t => t.Situacao == SituacaoDoTreinamento.Vencido);
        Assert.Equal("Impressora 3D FDM", antigo.TipoNome);
        Assert.Equal("Carla Coordenadora", antigo.RegistradoPorNome);

        var daCarla = await MeusTreinamentosAsync(fabrica, EmailDaCarla, senha);
        var dela = Assert.Single(daCarla);
        Assert.Equal(SituacaoDoTreinamento.Valido, dela.Situacao);
        Assert.Equal("Cortadora a laser", dela.TipoNome);
    }

    [Fact]
    public async Task Subir_de_novo_nao_duplica_os_treinamentos()
    {
        var connectionString = await bancos.NovoBancoAsync();
        var senha = AuxiliarDeContas.NovaSenha();
        await using (var primeira = Fabrica(connectionString, senha: senha))
        {
            using var cliente = primeira.CreateClient();
        }

        await using var segunda = Fabrica(connectionString, senha: senha);
        using var outroCliente = segunda.CreateClient();

        Assert.Equal(9, await ContarAsync(connectionString, TotalDeTreinamentos));
    }

    [Fact]
    public async Task Sem_a_senha_nenhum_treinamento_entra_porque_nao_ha_pessoas()
    {
        var connectionString = await bancos.NovoBancoAsync();
        await using var fabrica = Fabrica(connectionString, senha: null);

        using var cliente = fabrica.CreateClient();

        Assert.Equal(0, await ContarAsync(connectionString, TotalDeTreinamentos));
    }

    [Fact]
    public async Task Tipo_renomeado_fica_de_fora_sem_erro_e_so_os_que_existem_entram()
    {
        var connectionString = await bancos.NovoBancoAsync();
        var senha = AuxiliarDeContas.NovaSenha();
        await using (var primeira = Fabrica(connectionString, aplicar: true, senha: null))
        {
            using var cliente = primeira.CreateClient();
        }

        await ExecutarAsync(connectionString, "UPDATE tipos_de_equipamento SET nome = 'Laser renomeado' WHERE nome = 'Cortadora a laser'");
        await using var segunda = Fabrica(connectionString, senha: senha);
        using var outroCliente = segunda.CreateClient();

        // Os 3 treinamentos da cortadora (Ana, Beto e Carla) saem; ficam 6.
        Assert.Equal(6, await ContarAsync(connectionString, TotalDeTreinamentos));
    }

    private static string PessoaComPapel(string email, string papel) =>
        $"SELECT count(*) FROM usuarios WHERE email = '{email}' AND papel = '{papel}'";
}
