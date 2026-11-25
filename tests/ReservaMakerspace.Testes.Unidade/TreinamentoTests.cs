using Microsoft.Extensions.Time.Testing;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Unidade;

public class TreinamentoTests
{
    private static readonly Guid Pessoa = Guid.NewGuid();
    private static readonly Guid Tipo = Guid.NewGuid();
    private static readonly Guid Equipe = Guid.NewGuid();

    private static FakeTimeProvider RelogioEm(int ano, int mes, int dia) =>
        new(new DateTimeOffset(ano, mes, dia, 12, 0, 0, TimeSpan.Zero));

    private static Treinamento Registrar(FakeTimeProvider relogio) =>
        Treinamento.Registrar(Pessoa, Tipo, Equipe, relogio.GetUtcNow());

    [Fact]
    public void Registrar_grava_a_data_do_registro_e_a_validade_de_12_meses()
    {
        var relogio = RelogioEm(2026, 11, 25);

        var treinamento = Registrar(relogio);

        Assert.Equal(relogio.GetUtcNow(), treinamento.RealizadoEm);
        Assert.Equal(relogio.GetUtcNow().AddMonths(12), treinamento.ValidoAte);
        Assert.Equal(Pessoa, treinamento.UsuarioId);
        Assert.Equal(Tipo, treinamento.TipoId);
        Assert.Equal(Equipe, treinamento.RegistradoPorId);
        Assert.Null(treinamento.RevogadoEm);
    }

    [Fact]
    public void Um_instante_antes_dos_12_meses_ainda_e_valido_e_nos_12_meses_exatos_esta_vencido()
    {
        var relogio = RelogioEm(2026, 11, 25);
        var treinamento = Registrar(relogio);

        Assert.Equal(SituacaoDoTreinamento.Valido, treinamento.SituacaoEm(relogio.GetUtcNow()));

        relogio.SetUtcNow(treinamento.ValidoAte - TimeSpan.FromTicks(1));
        Assert.Equal(SituacaoDoTreinamento.Valido, treinamento.SituacaoEm(relogio.GetUtcNow()));

        relogio.SetUtcNow(treinamento.ValidoAte);
        Assert.Equal(SituacaoDoTreinamento.Vencido, treinamento.SituacaoEm(relogio.GetUtcNow()));
    }

    [Fact]
    public void Treinamento_de_29_de_fevereiro_vale_ate_28_de_fevereiro_do_ano_seguinte()
    {
        var relogio = RelogioEm(2028, 2, 29);

        var treinamento = Registrar(relogio);

        Assert.Equal(new DateTimeOffset(2029, 2, 28, 12, 0, 0, TimeSpan.Zero), treinamento.ValidoAte);
        relogio.SetUtcNow(treinamento.ValidoAte - TimeSpan.FromSeconds(1));
        Assert.Equal(SituacaoDoTreinamento.Valido, treinamento.SituacaoEm(relogio.GetUtcNow()));
        relogio.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SituacaoDoTreinamento.Vencido, treinamento.SituacaoEm(relogio.GetUtcNow()));
    }

    [Fact]
    public void Revogar_muda_a_situacao_na_hora_mesmo_antes_de_vencer_e_guarda_autor_motivo_e_instante()
    {
        var relogio = RelogioEm(2026, 11, 25);
        var treinamento = Registrar(relogio);
        relogio.Advance(TimeSpan.FromDays(30));
        var revogador = Guid.NewGuid();

        treinamento.Revogar(revogador, "Descarte fora do recipiente.", relogio.GetUtcNow());

        Assert.Equal(SituacaoDoTreinamento.Revogado, treinamento.SituacaoEm(relogio.GetUtcNow()));
        Assert.Equal(relogio.GetUtcNow(), treinamento.RevogadoEm);
        Assert.Equal(revogador, treinamento.RevogadoPorId);
        Assert.Equal("Descarte fora do recipiente.", treinamento.MotivoDaRevogacao);
    }

    [Fact]
    public void A_revogacao_prevalece_sobre_o_vencimento_na_situacao()
    {
        var relogio = RelogioEm(2026, 11, 25);
        var treinamento = Registrar(relogio);
        relogio.Advance(TimeSpan.FromDays(1));
        treinamento.Revogar(Guid.NewGuid(), "Motivo.", relogio.GetUtcNow());

        relogio.SetUtcNow(treinamento.ValidoAte.AddDays(10));

        Assert.Equal(SituacaoDoTreinamento.Revogado, treinamento.SituacaoEm(relogio.GetUtcNow()));
    }

    [Fact]
    public void Revogar_duas_vezes_lanca_e_mantem_a_primeira_revogacao()
    {
        var relogio = RelogioEm(2026, 11, 25);
        var treinamento = Registrar(relogio);
        var primeiro = Guid.NewGuid();
        treinamento.Revogar(primeiro, "Primeiro motivo.", relogio.GetUtcNow());
        relogio.Advance(TimeSpan.FromDays(1));

        Assert.Throws<InvalidOperationException>(
            () => treinamento.Revogar(Guid.NewGuid(), "Segundo motivo.", relogio.GetUtcNow()));

        Assert.Equal(primeiro, treinamento.RevogadoPorId);
        Assert.Equal("Primeiro motivo.", treinamento.MotivoDaRevogacao);
    }

    [Fact]
    public void Revogar_um_vencido_lanca_e_ele_continua_vencido()
    {
        var relogio = RelogioEm(2026, 11, 25);
        var treinamento = Registrar(relogio);
        relogio.SetUtcNow(treinamento.ValidoAte);

        Assert.Throws<InvalidOperationException>(
            () => treinamento.Revogar(Guid.NewGuid(), "Tarde demais.", relogio.GetUtcNow()));

        Assert.Equal(SituacaoDoTreinamento.Vencido, treinamento.SituacaoEm(relogio.GetUtcNow()));
        Assert.Null(treinamento.RevogadoEm);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Revogar_sem_motivo_lanca_argument_exception(string motivo)
    {
        var relogio = RelogioEm(2026, 11, 25);
        var treinamento = Registrar(relogio);

        var erro = Assert.Throws<ArgumentException>(
            () => treinamento.Revogar(Guid.NewGuid(), motivo, relogio.GetUtcNow()));

        Assert.Equal("motivo", erro.ParamName);
        Assert.Null(treinamento.RevogadoEm);
    }
}
