using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Unidade;

public class IntervaloTests
{
    private static DateTimeOffset Hora(int hora, int minuto = 0) =>
        new(2026, 12, 1, hora, minuto, 0, TimeSpan.Zero);

    private static Intervalo De(int inicio, int fim) => new(Hora(inicio), Hora(fim));

    [Theory]
    [InlineData(10, 10)]
    [InlineData(10, 9)]
    public void Fim_igual_ou_antes_do_inicio_lanca(int inicio, int fim)
    {
        var erro = Assert.Throws<ArgumentException>(() => new Intervalo(Hora(inicio), Hora(fim)));

        Assert.Equal("fim", erro.ParamName);
        Assert.StartsWith("O fim precisa ser depois do início.", erro.Message);
    }

    [Fact]
    public void Adjacentes_nao_se_sobrepoem()
    {
        var a = De(10, 11);
        var b = De(11, 12);

        Assert.False(a.SeSobrepoeA(b));
        Assert.False(b.SeSobrepoeA(a));
    }

    [Fact]
    public void Cruzados_se_sobrepoem()
    {
        var a = De(10, 12);
        var b = De(11, 13);

        Assert.True(a.SeSobrepoeA(b));
        Assert.True(b.SeSobrepoeA(a));
    }

    [Fact]
    public void Contido_se_sobrepoe()
    {
        var externo = De(10, 14);
        var interno = De(11, 12);

        Assert.True(externo.SeSobrepoeA(interno));
        Assert.True(interno.SeSobrepoeA(externo));
    }

    [Fact]
    public void Separados_nao_se_sobrepoem()
    {
        var a = De(10, 11);
        var b = De(12, 13);

        Assert.False(a.SeSobrepoeA(b));
        Assert.False(b.SeSobrepoeA(a));
    }

    [Fact]
    public void Compara_instantes_e_nao_o_fuso()
    {
        var fusoBrasil = TimeSpan.FromHours(-3);
        // 10:00-03:00 a 11:00-03:00 equivale a 13:00Z a 14:00Z.
        var local = new Intervalo(
            new DateTimeOffset(2026, 12, 1, 10, 0, 0, fusoBrasil),
            new DateTimeOffset(2026, 12, 1, 11, 0, 0, fusoBrasil));
        var utcSobreposto = new Intervalo(Hora(13, 30), Hora(14));
        var utcDepois = new Intervalo(Hora(14), Hora(15));

        Assert.True(local.SeSobrepoeA(utcSobreposto));
        Assert.False(local.SeSobrepoeA(utcDepois));
    }

    [Fact]
    public void Duracao_e_a_diferenca()
    {
        var intervalo = new Intervalo(Hora(10), Hora(12, 30));

        Assert.Equal(TimeSpan.FromMinutes(150), intervalo.Duracao);
    }
}
