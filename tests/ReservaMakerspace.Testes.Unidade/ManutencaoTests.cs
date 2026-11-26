using Microsoft.Extensions.Time.Testing;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Unidade;

/// <summary>O registro e o encerramento de uma manutenção, com o relógio falso e as bordas (plano do 1.7, decisões 2 e 4).</summary>
public class ManutencaoTests
{
    private static readonly Guid Maquina = Guid.NewGuid();
    private static readonly Guid Quem = Guid.NewGuid();
    private static readonly Guid Outro = Guid.NewGuid();

    private static readonly DateTimeOffset T0 = new(2027, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static Manutencao Registrar(
        DateTimeOffset? inicio, DateTimeOffset fim, string? motivo = "Troca do bico.", DateTimeOffset? agora = null) =>
        Manutencao.Registrar(Maquina, Quem, inicio, fim, motivo, agora ?? T0);

    [Fact]
    public void Registrar_guarda_a_maquina_o_intervalo_o_motivo_e_quem_registrou()
    {
        var manutencao = Registrar(T0.AddHours(2), T0.AddHours(5));

        Assert.NotEqual(Guid.Empty, manutencao.Id);
        Assert.Equal(Maquina, manutencao.EquipamentoId);
        Assert.Equal(T0.AddHours(2), manutencao.Inicio);
        Assert.Equal(T0.AddHours(5), manutencao.Fim);
        Assert.Equal("Troca do bico.", manutencao.Motivo);
        Assert.Equal(SituacaoDaManutencao.Ativa, manutencao.Situacao);
        Assert.Equal(T0, manutencao.RegistradaEm);
        Assert.Equal(Quem, manutencao.RegistradaPorId);
        Assert.Null(manutencao.EncerradaEm);
        Assert.Null(manutencao.EncerradaPorId);
    }

    [Fact]
    public void Registrar_apara_o_motivo_e_grava_os_horarios_em_utc()
    {
        var emBrasilia = TimeSpan.FromHours(-3);
        var manutencao = Registrar(
            new DateTimeOffset(2027, 3, 10, 11, 0, 0, emBrasilia),
            new DateTimeOffset(2027, 3, 10, 13, 0, 0, emBrasilia),
            "  Troca do bico.  ");

        Assert.Equal("Troca do bico.", manutencao.Motivo);
        Assert.Equal(TimeSpan.Zero, manutencao.Inicio.Offset);
        Assert.Equal(TimeSpan.Zero, manutencao.Fim.Offset);
        Assert.Equal(new DateTimeOffset(2027, 3, 10, 14, 0, 0, TimeSpan.Zero), manutencao.Inicio);
    }

    [Fact]
    public void Inicio_igual_ao_agora_e_aceito()
    {
        var manutencao = Registrar(T0, T0.AddHours(1));

        Assert.Equal(T0, manutencao.Inicio);
    }

    [Fact]
    public void Inicio_um_segundo_antes_do_agora_e_recusado()
    {
        var erro = Assert.Throws<ManutencaoRecusadaException>(() => Registrar(T0.AddSeconds(-1), T0.AddHours(1)));

        Assert.Equal("O início da manutenção não pode ser no passado.", erro.Message);
    }

    [Fact]
    public void Sem_inicio_a_manutencao_comeca_no_agora()
    {
        var manutencao = Registrar(inicio: null, T0.AddHours(3));

        Assert.Equal(T0, manutencao.Inicio);
        Assert.Equal(T0.AddHours(3), manutencao.Fim);
    }

    [Fact]
    public void Sem_inicio_o_agora_acompanha_o_relogio()
    {
        var relogio = new FakeTimeProvider(T0);
        relogio.Advance(TimeSpan.FromMinutes(7));

        var manutencao = Manutencao.Registrar(Maquina, Quem, null, T0.AddHours(3), "Quebrou.", relogio.GetUtcNow());

        Assert.Equal(T0.AddMinutes(7), manutencao.Inicio);
    }

    [Fact]
    public void Sem_inicio_e_com_fim_no_agora_ou_antes_e_argumento_invalido()
    {
        Assert.Throws<ArgumentException>(() => Registrar(inicio: null, T0));
        Assert.Throws<ArgumentException>(() => Registrar(inicio: null, T0.AddSeconds(-1)));
    }

    [Fact]
    public void Fim_igual_ou_antes_do_inicio_e_argumento_invalido_e_vem_antes_da_regra_do_passado()
    {
        Assert.Throws<ArgumentException>(() => Registrar(T0.AddHours(2), T0.AddHours(2)));
        Assert.Throws<ArgumentException>(() => Registrar(T0.AddHours(2), T0.AddHours(1)));
        // Intervalo inválido e no passado: vale o 400, e não o 422.
        Assert.Throws<ArgumentException>(() => Registrar(T0.AddHours(-2), T0.AddHours(-3)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Motivo_ausente_ou_so_com_espacos_e_argumento_invalido(string? motivo)
    {
        Assert.Throws<ArgumentException>(() => Registrar(T0.AddHours(1), T0.AddHours(2), motivo));
    }

    [Fact]
    public void Motivo_com_500_caracteres_passa_e_com_501_nao()
    {
        Assert.Equal(500, Registrar(T0.AddHours(1), T0.AddHours(2), new string('a', 500)).Motivo.Length);
        Assert.Throws<ArgumentException>(() => Registrar(T0.AddHours(1), T0.AddHours(2), new string('a', 501)));
    }

    [Fact]
    public void Encerrar_em_andamento_poe_o_fim_no_agora_e_continua_ativa()
    {
        var manutencao = Registrar(T0.AddHours(1), T0.AddHours(5));

        manutencao.Encerrar(Outro, T0.AddHours(2));

        Assert.Equal(SituacaoDaManutencao.Ativa, manutencao.Situacao);
        Assert.Equal(T0.AddHours(1), manutencao.Inicio);
        Assert.Equal(T0.AddHours(2), manutencao.Fim);
        Assert.Equal(T0.AddHours(2), manutencao.EncerradaEm);
        Assert.Equal(Outro, manutencao.EncerradaPorId);
    }

    [Fact]
    public void Encerrar_um_segundo_depois_do_inicio_encurta_em_vez_de_cancelar()
    {
        var manutencao = Registrar(T0.AddHours(1), T0.AddHours(5));

        manutencao.Encerrar(Outro, T0.AddHours(1).AddSeconds(1));

        Assert.Equal(SituacaoDaManutencao.Ativa, manutencao.Situacao);
        Assert.Equal(T0.AddHours(1).AddSeconds(1), manutencao.Fim);
    }

    [Fact]
    public void Encerrar_um_segundo_antes_do_fim_encurta()
    {
        var manutencao = Registrar(T0.AddHours(1), T0.AddHours(5));

        manutencao.Encerrar(Outro, T0.AddHours(5).AddSeconds(-1));

        Assert.Equal(SituacaoDaManutencao.Ativa, manutencao.Situacao);
        Assert.Equal(T0.AddHours(5).AddSeconds(-1), manutencao.Fim);
    }

    [Fact]
    public void Encerrar_antes_de_comecar_cancela_e_guarda_o_encerramento()
    {
        var manutencao = Registrar(T0.AddHours(3), T0.AddHours(5));

        manutencao.Encerrar(Outro, T0.AddHours(1));

        Assert.Equal(SituacaoDaManutencao.Cancelada, manutencao.Situacao);
        // O intervalo fica como foi registrado.
        Assert.Equal(T0.AddHours(3), manutencao.Inicio);
        Assert.Equal(T0.AddHours(5), manutencao.Fim);
        Assert.Equal(T0.AddHours(1), manutencao.EncerradaEm);
        Assert.Equal(Outro, manutencao.EncerradaPorId);
    }

    [Fact]
    public void Encerrar_exatamente_no_inicio_cancela_porque_encurtar_deixaria_o_intervalo_vazio()
    {
        var manutencao = Registrar(T0.AddHours(3), T0.AddHours(5));

        manutencao.Encerrar(Outro, T0.AddHours(3));

        Assert.Equal(SituacaoDaManutencao.Cancelada, manutencao.Situacao);
        Assert.Equal(T0.AddHours(5), manutencao.Fim);
    }

    [Fact]
    public void Encerrar_no_instante_do_registro_sem_inicio_cancela()
    {
        // "Parar a máquina agora" e desfazer no mesmo instante: o início é o agora.
        var manutencao = Registrar(inicio: null, T0.AddHours(5));

        manutencao.Encerrar(Outro, T0);

        Assert.Equal(SituacaoDaManutencao.Cancelada, manutencao.Situacao);
    }

    [Fact]
    public void Encerrar_exatamente_no_fim_ou_depois_e_recusado_porque_ja_acabou()
    {
        var noFim = Registrar(T0.AddHours(1), T0.AddHours(5));
        var depois = Registrar(T0.AddHours(1), T0.AddHours(5));

        Assert.Equal(
            "A manutenção já acabou.",
            Assert.Throws<InvalidOperationException>(() => noFim.Encerrar(Outro, T0.AddHours(5))).Message);
        Assert.Equal(
            "A manutenção já acabou.",
            Assert.Throws<InvalidOperationException>(() => depois.Encerrar(Outro, T0.AddDays(2))).Message);
        Assert.Equal(SituacaoDaManutencao.Ativa, noFim.Situacao);
        Assert.Null(noFim.EncerradaEm);
    }

    [Fact]
    public void Encerrar_duas_vezes_a_cancelada_e_recusado_e_o_primeiro_encerramento_fica()
    {
        var manutencao = Registrar(T0.AddHours(3), T0.AddHours(5));
        manutencao.Encerrar(Outro, T0.AddHours(1));

        var erro = Assert.Throws<InvalidOperationException>(() => manutencao.Encerrar(Quem, T0.AddHours(2)));

        Assert.Equal("A manutenção já foi cancelada.", erro.Message);
        Assert.Equal(Outro, manutencao.EncerradaPorId);
        Assert.Equal(T0.AddHours(1), manutencao.EncerradaEm);
    }

    [Fact]
    public void Uma_cancelada_cujo_fim_ja_passou_diz_que_foi_cancelada()
    {
        var manutencao = Registrar(T0.AddHours(3), T0.AddHours(5));
        manutencao.Encerrar(Outro, T0.AddHours(1));

        var erro = Assert.Throws<InvalidOperationException>(() => manutencao.Encerrar(Quem, T0.AddHours(9)));

        Assert.Equal("A manutenção já foi cancelada.", erro.Message);
    }

    [Fact]
    public void Uma_encurtada_no_agora_ja_acabou_no_mesmo_instante()
    {
        var manutencao = Registrar(T0.AddHours(1), T0.AddHours(5));
        manutencao.Encerrar(Outro, T0.AddHours(2));

        // O fim agora é T0 + 2 h.
        Assert.Equal(
            "A manutenção já acabou.",
            Assert.Throws<InvalidOperationException>(() => manutencao.Encerrar(Quem, T0.AddHours(2))).Message);
    }

    [Fact]
    public void O_encerramento_acompanha_o_relogio_falso()
    {
        var relogio = new FakeTimeProvider(T0);
        var antes = Registrar(T0.AddHours(1), T0.AddHours(5));
        var depois = Registrar(T0.AddHours(1), T0.AddHours(5));

        antes.Encerrar(Outro, relogio.GetUtcNow());
        relogio.Advance(TimeSpan.FromHours(2));
        depois.Encerrar(Outro, relogio.GetUtcNow());

        Assert.Equal(SituacaoDaManutencao.Cancelada, antes.Situacao);
        Assert.Equal(SituacaoDaManutencao.Ativa, depois.Situacao);
        Assert.Equal(T0.AddHours(2), depois.Fim);
    }
}
