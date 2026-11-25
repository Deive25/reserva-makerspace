namespace ReservaMakerspace.Testes.Integracao;

public class SubidaTests
{
    private const string ConnectionStringQualquer = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x";

    [Fact]
    public void Subida_sem_connection_string_falha_com_mensagem_clara()
    {
        using var fabrica = new FabricaSemBanco(new Dictionary<string, string?>
        {
            ["Banco:AplicarMigracoesNaSubida"] = "false",
        });

        var erro = Assert.Throws<InvalidOperationException>(() => fabrica.CreateClient());

        Assert.Contains("ConnectionStrings__Makerspace", erro.Message);
    }

    [Fact]
    public void Subida_sem_chave_do_jwt_falha_com_mensagem_clara()
    {
        using var fabrica = new FabricaSemBanco(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Makerspace"] = ConnectionStringQualquer,
            ["Banco:AplicarMigracoesNaSubida"] = "false",
        });

        var erro = Assert.Throws<InvalidOperationException>(() => fabrica.CreateClient());

        Assert.Contains("Jwt__Chave", erro.Message);
    }

    [Fact]
    public void Subida_com_chave_do_jwt_curta_falha_com_mensagem_clara()
    {
        using var fabrica = new FabricaSemBanco(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Makerspace"] = ConnectionStringQualquer,
            // 31 bytes: um a menos que o mínimo.
            ["Jwt:Chave"] = new string('a', 31),
            ["Banco:AplicarMigracoesNaSubida"] = "false",
        });

        var erro = Assert.Throws<InvalidOperationException>(() => fabrica.CreateClient());

        Assert.Contains("Jwt__Chave", erro.Message);
        Assert.Contains("32", erro.Message);
    }
}
