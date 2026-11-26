using Microsoft.Extensions.Time.Testing;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Testes.Unidade;

/// <summary>As regras 1, 2, 3, 5, 6 e 7 da §4.2 e o cancelamento, com o relógio controlado.</summary>
public class ReservaTests
{
    private static readonly Guid Pessoa = Guid.NewGuid();
    private static readonly Guid Outra = Guid.NewGuid();

    private static readonly DateTimeOffset Agora = new(2027, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static Equipamento Maquina(bool ativa = true, int minutosMaximos = 120) => new()
    {
        Id = Guid.NewGuid(),
        Nome = "Impressora FDM 1",
        TipoId = Guid.NewGuid(),
        Tipo = new TipoDeEquipamento { Nome = "Impressora 3D FDM", DuracaoMaximaPessoal = TimeSpan.FromMinutes(minutosMaximos) },
        Ativo = ativa,
    };

    private static Reserva Pessoal(
        DateTimeOffset inicio,
        DateTimeOffset fim,
        Equipamento? maquina = null,
        DateTimeOffset? treinadoAte = null,
        int pessoaisAtivas = 0,
        Papel papel = Papel.Usuario,
        string? solicitante = null) =>
        Reserva.Criar(
            maquina ?? Maquina(),
            Pessoa,
            papel,
            FinalidadeDaReserva.ProjetoPessoal,
            inicio,
            fim,
            solicitante,
            descricao: null,
            treinadoAte: treinadoAte ?? Agora.AddMonths(6),
            reservasPessoaisAtivasEFuturas: pessoaisAtivas,
            agora: Agora);

    private static Reserva Demanda(
        DateTimeOffset inicio,
        DateTimeOffset fim,
        Papel papel = Papel.Equipe,
        Equipamento? maquina = null) =>
        Reserva.Criar(
            maquina ?? Maquina(),
            Pessoa,
            papel,
            FinalidadeDaReserva.DemandaDoParque,
            inicio,
            fim,
            "Prof. Daniel",
            descricao: null,
            treinadoAte: Agora.AddMonths(6),
            reservasPessoaisAtivasEFuturas: 0,
            agora: Agora);

    private static ReservaRecusadaException Recusa(Action acao) => Assert.Throws<ReservaRecusadaException>(acao);

    [Fact]
    public void Criar_grava_os_dados_em_UTC_a_situacao_ativa_e_a_data_de_criacao()
    {
        var maquina = Maquina();
        var inicio = new DateTimeOffset(2027, 3, 10, 14, 0, 0, TimeSpan.FromHours(-3));

        var reserva = Pessoal(inicio, inicio.AddHours(1), maquina);

        Assert.NotEqual(Guid.Empty, reserva.Id);
        Assert.Equal(maquina.Id, reserva.EquipamentoId);
        Assert.Equal(Pessoa, reserva.OperadorId);
        Assert.Equal(TimeSpan.Zero, reserva.Inicio.Offset);
        Assert.Equal(TimeSpan.Zero, reserva.Fim.Offset);
        Assert.Equal(new DateTimeOffset(2027, 3, 10, 17, 0, 0, TimeSpan.Zero), reserva.Inicio);
        Assert.Equal(new DateTimeOffset(2027, 3, 10, 18, 0, 0, TimeSpan.Zero), reserva.Fim);
        Assert.Equal(SituacaoDaReserva.Ativa, reserva.Situacao);
        Assert.Equal(Agora, reserva.CriadaEm);
        Assert.Null(reserva.CanceladaEm);
    }

    [Fact]
    public void Texto_so_com_espacos_vira_nulo_e_o_resto_e_aparado()
    {
        var reserva = Reserva.Criar(
            Maquina(), Pessoa, Papel.Equipe, FinalidadeDaReserva.DemandaDoParque,
            Agora.AddHours(1), Agora.AddHours(2), "   ", "  Suporte de celular  ",
            Agora.AddMonths(6), 0, Agora);

        Assert.Null(reserva.Solicitante);
        Assert.Equal("Suporte de celular", reserva.Descricao);
    }

    // Regra 1: início ≥ agora, fim > início.
    [Fact]
    public void Comecar_agora_e_aceito_e_um_segundo_antes_e_recusado()
    {
        Pessoal(Agora, Agora.AddHours(1));

        var erro = Recusa(() => Pessoal(Agora.AddSeconds(-1), Agora.AddHours(1)));
        Assert.Equal(MotivoDaRecusa.RegraDeNegocio, erro.Motivo);
        Assert.Equal("O início da reserva não pode ser no passado.", erro.Message);
    }

    [Fact]
    public void Fim_igual_ou_antes_do_inicio_e_argumento_invalido()
    {
        Assert.Throws<ArgumentException>(() => Pessoal(Agora.AddHours(1), Agora.AddHours(1)));
        Assert.Throws<ArgumentException>(() => Pessoal(Agora.AddHours(2), Agora.AddHours(1)));
    }

    // Regra 2.
    [Fact]
    public void Maquina_inativa_nao_aceita_reserva()
    {
        var erro = Recusa(() => Pessoal(Agora.AddHours(1), Agora.AddHours(2), Maquina(ativa: false)));

        Assert.Equal(MotivoDaRecusa.RegraDeNegocio, erro.Motivo);
        Assert.Equal("A máquina Impressora FDM 1 está inativa.", erro.Message);
    }

    // Regra 3.
    [Fact]
    public void Acabar_exatamente_no_fim_da_validade_do_treinamento_e_aceito_e_um_segundo_depois_e_recusado()
    {
        var inicio = Agora.AddHours(1);
        var fim = inicio.AddHours(1);

        Pessoal(inicio, fim, treinadoAte: fim);

        var erro = Recusa(() => Pessoal(inicio, fim, treinadoAte: fim.AddSeconds(-1)));
        Assert.Equal(MotivoDaRecusa.RegraDeNegocio, erro.Motivo);
        Assert.Equal("Sem treinamento válido em Impressora 3D FDM até o fim da reserva.", erro.Message);
    }

    [Fact]
    public void Sem_treinamento_nenhum_a_reserva_e_recusada()
    {
        var erro = Recusa(() => Reserva.Criar(
            Maquina(), Pessoa, Papel.Usuario, FinalidadeDaReserva.ProjetoPessoal,
            Agora.AddHours(1), Agora.AddHours(2), null, null, treinadoAte: null, 0, Agora));

        Assert.Equal("Sem treinamento válido em Impressora 3D FDM até o fim da reserva.", erro.Message);
    }

    // Regra 5.
    [Fact]
    public void Durar_exatamente_a_maxima_do_tipo_e_aceito_e_um_minuto_a_mais_e_recusado()
    {
        var inicio = Agora.AddHours(1);

        Pessoal(inicio, inicio.AddMinutes(120));

        var erro = Recusa(() => Pessoal(inicio, inicio.AddMinutes(121)));
        Assert.Equal(MotivoDaRecusa.RegraDeNegocio, erro.Motivo);
        Assert.Equal("A duração máxima de uma reserva pessoal em Impressora 3D FDM é 2 h.", erro.Message);
    }

    [Fact]
    public void Comecar_em_exatamente_7_dias_e_aceito_e_um_segundo_depois_e_recusado()
    {
        var limite = Agora.AddDays(7);

        Pessoal(limite, limite.AddHours(1));

        var erro = Recusa(() => Pessoal(limite.AddSeconds(1), limite.AddHours(1).AddSeconds(1)));
        Assert.Equal("Uma reserva pessoal só pode começar em até 7 dias.", erro.Message);
    }

    [Fact]
    public void A_terceira_reserva_pessoal_ativa_e_futura_e_recusada()
    {
        var inicio = Agora.AddHours(1);

        Pessoal(inicio, inicio.AddHours(1), pessoaisAtivas: 1);

        var erro = Recusa(() => Pessoal(inicio, inicio.AddHours(1), pessoaisAtivas: 2));
        Assert.Equal("Você já tem 2 reservas pessoais ativas. Cancele uma ou espere uma acabar.", erro.Message);
    }

    // Regra 6.
    [Fact]
    public void Demanda_do_parque_nao_tem_duracao_maxima_e_comeca_em_ate_30_dias()
    {
        var limite = Agora.AddDays(30);

        var longa = Demanda(Agora.AddHours(1), Agora.AddHours(11));
        Demanda(limite, limite.AddHours(1));

        Assert.Equal(FinalidadeDaReserva.DemandaDoParque, longa.Finalidade);
        Assert.Equal("Prof. Daniel", longa.Solicitante);
        var erro = Recusa(() => Demanda(limite.AddSeconds(1), limite.AddHours(1)));
        Assert.Equal("Uma demanda do parque só pode começar em até 30 dias.", erro.Message);
    }

    [Fact]
    public void Demanda_do_parque_aceita_a_Coordenacao_e_nao_e_limitada_pelo_projeto_pessoal()
    {
        Demanda(Agora.AddDays(20), Agora.AddDays(20).AddHours(1), Papel.Coordenacao);

        // Mesmo com 2 pessoais ativas, a demanda passa: o limite é só dos projetos pessoais.
        var demanda = Reserva.Criar(
            Maquina(), Pessoa, Papel.Equipe, FinalidadeDaReserva.DemandaDoParque,
            Agora.AddHours(1), Agora.AddHours(2), null, null, Agora.AddMonths(6), 2, Agora);
        Assert.Equal(SituacaoDaReserva.Ativa, demanda.Situacao);
    }

    // Regra 7.
    [Fact]
    public void Usuario_pedindo_demanda_do_parque_e_recusado_por_falta_de_permissao()
    {
        var erro = Recusa(() => Demanda(Agora.AddHours(1), Agora.AddHours(2), Papel.Usuario));

        Assert.Equal(MotivoDaRecusa.SemPermissao, erro.Motivo);
    }

    [Fact]
    public void A_permissao_e_conferida_antes_das_regras_de_negocio()
    {
        // Usuário, demanda, máquina inativa e início no passado: o primeiro erro é o de permissão.
        var erro = Recusa(() => Demanda(Agora.AddDays(-1), Agora.AddDays(-1).AddHours(1), Papel.Usuario, Maquina(ativa: false)));

        Assert.Equal(MotivoDaRecusa.SemPermissao, erro.Motivo);
    }

    [Fact]
    public void Solicitante_so_existe_na_demanda_do_parque()
    {
        Assert.Throws<ArgumentException>(() => Pessoal(Agora.AddHours(1), Agora.AddHours(2), solicitante: "Prof. Daniel"));
    }

    // Cancelamento.
    [Fact]
    public void O_dono_cancela_antes_do_inicio_e_a_reserva_guarda_a_data_e_o_autor()
    {
        var reserva = Pessoal(Agora.AddHours(1), Agora.AddHours(2));

        reserva.Cancelar(SituacaoDaReserva.CanceladaPeloDono, Pessoa, motivo: null, Agora);

        Assert.Equal(SituacaoDaReserva.CanceladaPeloDono, reserva.Situacao);
        Assert.Equal(Agora, reserva.CanceladaEm);
        Assert.Equal(Pessoa, reserva.CanceladaPorId);
        Assert.Null(reserva.MotivoDoCancelamento);
    }

    [Fact]
    public void O_dono_nao_cancela_a_reserva_que_ja_comecou_nem_a_ja_cancelada()
    {
        var reserva = Pessoal(Agora.AddHours(1), Agora.AddHours(2));

        var comecou = Assert.Throws<InvalidOperationException>(() =>
            reserva.Cancelar(SituacaoDaReserva.CanceladaPeloDono, Pessoa, null, Agora.AddHours(1)));
        Assert.Equal("A reserva já começou.", comecou.Message);

        reserva.Cancelar(SituacaoDaReserva.CanceladaPeloDono, Pessoa, null, Agora);
        var jaCancelada = Assert.Throws<InvalidOperationException>(() =>
            reserva.Cancelar(SituacaoDaReserva.CanceladaPeloDono, Pessoa, null, Agora));
        Assert.Equal("A reserva já foi cancelada.", jaCancelada.Message);
    }

    [Fact]
    public void A_equipe_cancela_com_motivo_e_o_motivo_e_obrigatorio()
    {
        var reserva = Pessoal(Agora.AddHours(1), Agora.AddHours(2));

        Assert.Throws<ArgumentException>(() =>
            reserva.Cancelar(SituacaoDaReserva.CanceladaPelaEquipe, Outra, "  ", Agora));

        reserva.Cancelar(SituacaoDaReserva.CanceladaPelaEquipe, Outra, " Treinamento revogado ", Agora);

        Assert.Equal(SituacaoDaReserva.CanceladaPelaEquipe, reserva.Situacao);
        Assert.Equal(Outra, reserva.CanceladaPorId);
        Assert.Equal("Treinamento revogado", reserva.MotivoDoCancelamento);
    }

    [Fact]
    public void Cancelar_com_a_situacao_ativa_e_argumento_invalido()
    {
        var reserva = Pessoal(Agora.AddHours(1), Agora.AddHours(2));

        Assert.Throws<ArgumentException>(() => reserva.Cancelar(SituacaoDaReserva.Ativa, Pessoa, null, Agora));
    }

    [Fact]
    public void A_regra_acompanha_o_relogio_falso()
    {
        var relogio = new FakeTimeProvider(Agora);
        var inicio = Agora.AddHours(1);
        Reserva.Criar(Maquina(), Pessoa, Papel.Usuario, FinalidadeDaReserva.ProjetoPessoal,
            inicio, inicio.AddHours(1), null, null, Agora.AddMonths(6), 0, relogio.GetUtcNow());

        relogio.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));

        Recusa(() => Reserva.Criar(Maquina(), Pessoa, Papel.Usuario, FinalidadeDaReserva.ProjetoPessoal,
            inicio, inicio.AddHours(1), null, null, Agora.AddMonths(6), 0, relogio.GetUtcNow()));
    }
}
