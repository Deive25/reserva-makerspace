using Microsoft.Extensions.Time.Testing;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Unidade;

/// <summary>As travas da §4.3 da spec, com o relógio falso e as bordas das 24 h (plano do 1.6, decisão 5).</summary>
public class DesmarcacaoTests
{
    private static readonly DateTimeOffset T0 = new(2027, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly FakeTimeProvider Relogio = new(T0);

    private static ReservaEmConflito Pessoal(TimeSpan comecaDaquiA) =>
        new(T0 + comecaDaquiA, FinalidadeDaReserva.ProjetoPessoal);

    private static ReservaEmConflito Demanda(TimeSpan comecaDaquiA) =>
        new(T0 + comecaDaquiA, FinalidadeDaReserva.DemandaDoParque);

    private static RecusaDaDesmarcacao? Avaliar(
        ReservaEmConflito[] conflitos,
        string? motivo = "Visita técnica.",
        string? maquinaLivre = null,
        bool haManutencao = false) =>
        Desmarcacao.Avaliar(conflitos, motivo, maquinaLivre, Relogio.GetUtcNow(), haManutencao);

    [Fact]
    public void A_antecedencia_minima_e_de_24_horas()
    {
        Assert.Equal(24, Desmarcacao.HorasMinimasDeAntecedencia);
    }

    [Fact]
    public void Pessoal_que_comeca_daqui_a_24_h_e_1_s_pode_ser_desmarcada()
    {
        Assert.Null(Avaliar([Pessoal(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1))]));
    }

    [Fact]
    public void Pessoal_que_comeca_daqui_a_exatamente_24_h_nao_pode()
    {
        var recusa = Avaliar([Pessoal(TimeSpan.FromHours(24))]);

        Assert.NotNull(recusa);
        Assert.Equal(TravaDaDesmarcacao.AntecedenciaMinima, recusa.Trava);
        Assert.Equal("Só dá para desmarcar reservas que começam daqui a mais de 24 h.", recusa.Mensagem);
    }

    [Fact]
    public void Pessoal_que_comeca_daqui_a_23_h_59_min_59_s_nao_pode()
    {
        var recusa = Avaliar([Pessoal(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1))]);

        Assert.Equal(TravaDaDesmarcacao.AntecedenciaMinima, recusa?.Trava);
    }

    [Fact]
    public void Pessoal_em_andamento_nao_pode()
    {
        var recusa = Avaliar([Pessoal(TimeSpan.FromHours(-1))]);

        Assert.Equal(TravaDaDesmarcacao.AntecedenciaMinima, recusa?.Trava);
    }

    [Fact]
    public void Basta_uma_pessoal_dentro_das_24_h_para_recusar_todas()
    {
        var recusa = Avaliar([Pessoal(TimeSpan.FromDays(3)), Pessoal(TimeSpan.FromHours(24))]);

        Assert.Equal(TravaDaDesmarcacao.AntecedenciaMinima, recusa?.Trava);
    }

    [Fact]
    public void Varias_pessoais_com_mais_de_24_h_podem_ser_desmarcadas()
    {
        Assert.Null(Avaliar([Pessoal(TimeSpan.FromDays(2)), Pessoal(TimeSpan.FromDays(2) + TimeSpan.FromHours(1))]));
    }

    [Fact]
    public void Demanda_do_parque_em_conflito_recusa_mesmo_longe()
    {
        var recusa = Avaliar([Demanda(TimeSpan.FromDays(5))]);

        Assert.NotNull(recusa);
        Assert.Equal(TravaDaDesmarcacao.DemandaNaoSeDesmarca, recusa.Trava);
        Assert.Equal("Uma demanda do parque não desmarca outra.", recusa.Mensagem);
    }

    [Fact]
    public void Demanda_junto_com_pessoais_recusa_pela_demanda()
    {
        var recusa = Avaliar([Pessoal(TimeSpan.FromDays(3)), Demanda(TimeSpan.FromDays(3))]);

        Assert.Equal(TravaDaDesmarcacao.DemandaNaoSeDesmarca, recusa?.Trava);
    }

    [Fact]
    public void A_manutencao_recusa_com_motivo_mesmo_longe_e_sem_outra_trava()
    {
        var recusa = Avaliar([Pessoal(TimeSpan.FromDays(3))], haManutencao: true);

        Assert.NotNull(recusa);
        Assert.Equal(TravaDaDesmarcacao.ManutencaoNaoSeDesmarca, recusa.Trava);
        Assert.Equal("Uma manutenção não se desmarca.", recusa.Mensagem);
    }

    [Fact]
    public void A_manutencao_recusa_mesmo_sem_nenhuma_reserva_em_conflito()
    {
        var recusa = Avaliar([], haManutencao: true);

        Assert.Equal(TravaDaDesmarcacao.ManutencaoNaoSeDesmarca, recusa?.Trava);
    }

    [Fact]
    public void A_manutencao_vem_antes_da_demanda_da_antecedencia_e_da_maquina_livre()
    {
        var recusa = Avaliar(
            [Demanda(TimeSpan.FromHours(1)), Pessoal(TimeSpan.FromHours(1))],
            maquinaLivre: "Impressora FDM 2",
            haManutencao: true);

        Assert.Equal(TravaDaDesmarcacao.ManutencaoNaoSeDesmarca, recusa?.Trava);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Sem_motivo_a_manutencao_volta_como_409_simples_com_a_mensagem_da_manutencao(string? motivo)
    {
        var recusa = Avaliar([Demanda(TimeSpan.FromHours(1))], motivo, haManutencao: true);

        Assert.NotNull(recusa);
        Assert.Equal(TravaDaDesmarcacao.MotivoObrigatorio, recusa.Trava);
        Assert.Equal("A máquina está em manutenção nesse horário.", recusa.Mensagem);
    }

    [Fact]
    public void Com_outra_maquina_livre_recusa_e_cita_a_maquina()
    {
        var recusa = Avaliar([Pessoal(TimeSpan.FromDays(3))], maquinaLivre: "Impressora FDM 2");

        Assert.NotNull(recusa);
        Assert.Equal(TravaDaDesmarcacao.HaMaquinaLivre, recusa.Trava);
        Assert.Equal("A Impressora FDM 2 está livre nesse horário. Reserve-a em vez de desmarcar.", recusa.Mensagem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sem_motivo_o_conflito_volta_como_esta(string? motivo)
    {
        var recusa = Avaliar([Pessoal(TimeSpan.FromDays(3))], motivo);

        Assert.NotNull(recusa);
        Assert.Equal(TravaDaDesmarcacao.MotivoObrigatorio, recusa.Trava);
        Assert.Equal("A máquina já tem uma reserva ativa nesse horário.", recusa.Mensagem);
    }

    [Fact]
    public void O_motivo_vem_antes_das_outras_travas()
    {
        var recusa = Avaliar(
            [Demanda(TimeSpan.FromHours(1)), Pessoal(TimeSpan.FromHours(1))], motivo: " ", maquinaLivre: "Impressora FDM 2");

        Assert.Equal(TravaDaDesmarcacao.MotivoObrigatorio, recusa?.Trava);
    }

    [Fact]
    public void A_demanda_vem_antes_da_antecedencia_e_da_maquina_livre()
    {
        var recusa = Avaliar(
            [Demanda(TimeSpan.FromHours(1)), Pessoal(TimeSpan.FromHours(1))], maquinaLivre: "Impressora FDM 2");

        Assert.Equal(TravaDaDesmarcacao.DemandaNaoSeDesmarca, recusa?.Trava);
    }

    [Fact]
    public void A_antecedencia_vem_antes_da_maquina_livre()
    {
        var recusa = Avaliar([Pessoal(TimeSpan.FromHours(2))], maquinaLivre: "Impressora FDM 2");

        Assert.Equal(TravaDaDesmarcacao.AntecedenciaMinima, recusa?.Trava);
    }

    [Fact]
    public void A_borda_acompanha_o_relogio()
    {
        // Uma pessoal que começa daqui a 25 h passa; 2 h depois, ela começa daqui a 23 h e não passa mais.
        var conflito = new[] { new ReservaEmConflito(T0.AddHours(25), FinalidadeDaReserva.ProjetoPessoal) };
        var relogio = new FakeTimeProvider(T0);

        Assert.Null(Desmarcacao.Avaliar(conflito, "Visita.", null, relogio.GetUtcNow()));

        relogio.Advance(TimeSpan.FromHours(2));

        Assert.Equal(
            TravaDaDesmarcacao.AntecedenciaMinima,
            Desmarcacao.Avaliar(conflito, "Visita.", null, relogio.GetUtcNow())?.Trava);
    }
}
