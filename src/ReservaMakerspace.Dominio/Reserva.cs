namespace ReservaMakerspace.Dominio;

/// <summary>
/// Reserva de uma máquina por um operador, no intervalo semiaberto [Inicio, Fim) (spec §4.1). Os horários ficam
/// sempre em UTC. A regra 4 da §4.2 (conflito na agenda) não está aqui: depende das outras reservas, e quem a
/// garante é a API, com a trava da máquina e a restrição de exclusão do banco (ADR 0006).
/// O "agora" sempre chega por parâmetro (vem do <see cref="TimeProvider"/> da API), para as regras serem testáveis.
/// </summary>
public class Reserva
{
    public const int MaximoDePessoaisAtivas = 2;

    public const int DiasMaximosAteOInicioPessoal = 7;

    public const int DiasMaximosAteOInicioDaDemanda = 30;

    // Construtor para o EF Core materializar a linha; o resto do código usa Criar.
    private Reserva()
    {
    }

    public Guid Id { get; private set; }

    public Guid EquipamentoId { get; private set; }

    public Guid OperadorId { get; private set; }

    public DateTimeOffset Inicio { get; private set; }

    public DateTimeOffset Fim { get; private set; }

    public FinalidadeDaReserva Finalidade { get; private set; }

    /// <summary>Quem pediu ao bolsista, só na demanda do parque.</summary>
    public string? Solicitante { get; private set; }

    public string? Descricao { get; private set; }

    public SituacaoDaReserva Situacao { get; private set; }

    public DateTimeOffset CriadaEm { get; private set; }

    public DateTimeOffset? CanceladaEm { get; private set; }

    public Guid? CanceladaPorId { get; private set; }

    public string? MotivoDoCancelamento { get; private set; }

    /// <summary>
    /// Confere as regras 1, 2, 3, 5, 6 e 7 da §4.2 com o que a API leu do banco sob as travas. Primeiro o papel
    /// (<see cref="MotivoDaRecusa.SemPermissao"/>), depois as regras na ordem da spec
    /// (<see cref="MotivoDaRecusa.RegraDeNegocio"/>). Um intervalo ou um texto inválido é <see cref="ArgumentException"/>
    /// (a API o devolve como 400 antes de chegar aqui).
    /// </summary>
    /// <param name="maquina">A máquina, com o tipo carregado.</param>
    /// <param name="treinadoAte">O maior <c>ValidoAte</c> entre os treinamentos não revogados da pessoa no tipo da máquina.</param>
    /// <param name="reservasPessoaisAtivasEFuturas">Quantas reservas pessoais ativas da pessoa ainda não acabaram (<c>Fim &gt; agora</c>).</param>
    public static Reserva Criar(
        Equipamento maquina,
        Guid operadorId,
        Papel papelDoOperador,
        FinalidadeDaReserva finalidade,
        DateTimeOffset inicio,
        DateTimeOffset fim,
        string? solicitante,
        string? descricao,
        DateTimeOffset? treinadoAte,
        int reservasPessoaisAtivasEFuturas,
        DateTimeOffset agora)
    {
        // Regra 7.
        if (finalidade == FinalidadeDaReserva.DemandaDoParque && papelDoOperador == Papel.Usuario)
        {
            throw new ReservaRecusadaException(
                MotivoDaRecusa.SemPermissao, "Só a Equipe e a Coordenação criam uma demanda do parque.");
        }

        var intervalo = new Intervalo(inicio, fim);
        solicitante = Normalizar(solicitante);
        if (finalidade == FinalidadeDaReserva.ProjetoPessoal && solicitante is not null)
        {
            throw new ArgumentException("O solicitante só existe na demanda do parque.", nameof(solicitante));
        }

        // Regra 1.
        if (inicio < agora)
        {
            throw Regra("O início da reserva não pode ser no passado.");
        }

        // Regra 2.
        if (!maquina.Ativo)
        {
            throw Regra($"A máquina {maquina.Nome} está inativa.");
        }

        // Regra 3: vale até o fim da reserva, e não só no dia em que se reserva. A validade é semiaberta,
        // como a reserva: acabar exatamente no ValidoAte é aceito.
        if (treinadoAte is null || treinadoAte < fim)
        {
            throw Regra($"Sem treinamento válido em {maquina.Tipo.Nome} até o fim da reserva.");
        }

        if (finalidade == FinalidadeDaReserva.ProjetoPessoal)
        {
            // Regra 5.
            if (intervalo.Duracao > maquina.Tipo.DuracaoMaximaPessoal)
            {
                throw Regra(
                    $"A duração máxima de uma reserva pessoal em {maquina.Tipo.Nome} é {Descrever(maquina.Tipo.DuracaoMaximaPessoal)}.");
            }

            if (inicio > agora.AddDays(DiasMaximosAteOInicioPessoal))
            {
                throw Regra($"Uma reserva pessoal só pode começar em até {DiasMaximosAteOInicioPessoal} dias.");
            }

            if (reservasPessoaisAtivasEFuturas >= MaximoDePessoaisAtivas)
            {
                throw Regra(
                    $"Você já tem {MaximoDePessoaisAtivas} reservas pessoais ativas. Cancele uma ou espere uma acabar.");
            }
        }
        else if (inicio > agora.AddDays(DiasMaximosAteOInicioDaDemanda))
        {
            // Regra 6.
            throw Regra($"Uma demanda do parque só pode começar em até {DiasMaximosAteOInicioDaDemanda} dias.");
        }

        return new Reserva
        {
            Id = Guid.NewGuid(),
            EquipamentoId = maquina.Id,
            OperadorId = operadorId,
            // O Npgsql só grava timestamptz com offset 0.
            Inicio = inicio.ToUniversalTime(),
            Fim = fim.ToUniversalTime(),
            Finalidade = finalidade,
            Solicitante = solicitante,
            Descricao = Normalizar(descricao),
            Situacao = SituacaoDaReserva.Ativa,
            CriadaEm = agora,
        };
    }

    /// <summary>
    /// Cancela uma reserva ativa. O dono só cancela antes do início; a Equipe exige o motivo. Um cancelamento
    /// não sobrescreve outro (plano do 1.5, decisão 10). A API faz o mesmo com um <c>UPDATE</c> condicional, e
    /// este método serve ao domínio e aos dados de exemplo.
    /// </summary>
    public void Cancelar(SituacaoDaReserva situacao, Guid autorId, string? motivo, DateTimeOffset agora)
    {
        if (situacao == SituacaoDaReserva.Ativa)
        {
            throw new ArgumentException("Cancelar leva a uma situação de cancelamento.", nameof(situacao));
        }

        motivo = Normalizar(motivo);
        if (situacao == SituacaoDaReserva.CanceladaPelaEquipe && motivo is null)
        {
            throw new ArgumentException("Informe o motivo do cancelamento.", nameof(motivo));
        }

        if (Situacao != SituacaoDaReserva.Ativa)
        {
            throw new InvalidOperationException("A reserva já foi cancelada.");
        }

        if (situacao == SituacaoDaReserva.CanceladaPeloDono && Inicio <= agora)
        {
            throw new InvalidOperationException("A reserva já começou.");
        }

        Situacao = situacao;
        CanceladaEm = agora;
        CanceladaPorId = autorId;
        MotivoDoCancelamento = motivo;
    }

    /// <summary>Apara o texto; só espaços vira nulo.</summary>
    public static string? Normalizar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    private static ReservaRecusadaException Regra(string mensagem) => new(MotivoDaRecusa.RegraDeNegocio, mensagem);

    private static string Descrever(TimeSpan duracao)
    {
        var horas = (int)duracao.TotalHours;
        var minutos = duracao.Minutes;
        return (horas, minutos) switch
        {
            (0, _) => $"{minutos} min",
            (_, 0) => $"{horas} h",
            _ => $"{horas} h {minutos} min",
        };
    }
}
