namespace ReservaMakerspace.Testes.Integracao;

public class SubidaTests
{
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
}
