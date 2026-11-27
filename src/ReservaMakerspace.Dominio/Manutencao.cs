namespace ReservaMakerspace.Dominio;

/// <summary>
/// Manutenção de uma máquina, no intervalo semiaberto [Inicio, Fim) (spec §4.1 e §4.4). Enquanto está ativa, a máquina
/// não aceita reservas nesse intervalo; as reservas ativas que o cruzam são canceladas pela API, na mesma transação
/// do registro. Os horários ficam sempre em UTC. O "agora" sempre chega por parâmetro (vem do <see cref="TimeProvider"/>
/// da API), para as regras serem testáveis. Quem a garante contra outra manutenção e contra reservas é a API, com a
/// trava da máquina e a restrição de exclusão do banco (ADR 0008).
/// </summary>
public class Manutencao
{
    public const int TamanhoMaximoDoMotivo = 500;

    // Construtor para o EF Core materializar a linha; o resto do código usa Registrar.
    private Manutencao()
    {
    }

    public Guid Id { get; private set; }

    public Guid EquipamentoId { get; private set; }

    public DateTimeOffset Inicio { get; private set; }

    public DateTimeOffset Fim { get; private set; }

    public string Motivo { get; private set; } = "";

    public SituacaoDaManutencao Situacao { get; private set; }

    public DateTimeOffset RegistradaEm { get; private set; }

    public Guid RegistradaPorId { get; private set; }

    public DateTimeOffset? EncerradaEm { get; private set; }

    public Guid? EncerradaPorId { get; private set; }

    /// <summary>
    /// Registra uma manutenção. Sem <paramref name="inicio"/>, ela começa no <paramref name="agora"/> ("parar a máquina
    /// agora"); com ele, o início não pode ser no passado. Um intervalo ou um motivo inválido é
    /// <see cref="ArgumentException"/> (a API o devolve como 400); o início no passado é
    /// <see cref="ManutencaoRecusadaException"/> (422).
    /// </summary>
    public static Manutencao Registrar(
        Guid equipamentoId,
        Guid registradaPorId,
        DateTimeOffset? inicio,
        DateTimeOffset fim,
        string? motivo,
        DateTimeOffset agora)
    {
        motivo = Reserva.Normalizar(motivo);
        if (motivo is null)
        {
            throw new ArgumentException("Informe o motivo da manutenção.", nameof(motivo));
        }

        if (motivo.Length > TamanhoMaximoDoMotivo)
        {
            throw new ArgumentException(
                $"O motivo pode ter até {TamanhoMaximoDoMotivo} caracteres.", nameof(motivo));
        }

        var comeco = inicio ?? Truncar(agora);
        var intervalo = new Intervalo(comeco, fim);
        if (inicio is not null && inicio < agora)
        {
            throw new ManutencaoRecusadaException("O início da manutenção não pode ser no passado.");
        }

        return new Manutencao
        {
            Id = Guid.NewGuid(),
            EquipamentoId = equipamentoId,
            // O Npgsql só grava timestamptz com offset 0.
            Inicio = intervalo.Inicio.ToUniversalTime(),
            Fim = intervalo.Fim.ToUniversalTime(),
            Motivo = motivo,
            Situacao = SituacaoDaManutencao.Ativa,
            RegistradaEm = agora,
            RegistradaPorId = registradaPorId,
        };
    }

    /// <summary>
    /// Encerra a manutenção antes do fim (§4.4). Em andamento (<c>Inicio &lt; agora &lt; Fim</c>), o fim passa a ser o
    /// agora e ela continua ativa. Ainda não começou (<c>agora &lt;= Inicio</c>), fica cancelada: encurtar até o
    /// início deixaria o intervalo vazio. Já acabou ou já foi cancelada é <see cref="InvalidOperationException"/> (409).
    /// As reservas que ela cancelou não voltam.
    /// </summary>
    public void Encerrar(Guid encerradaPorId, DateTimeOffset agora)
    {
        if (Situacao == SituacaoDaManutencao.Cancelada)
        {
            throw new InvalidOperationException("A manutenção já foi cancelada.");
        }

        if (Fim <= agora)
        {
            throw new InvalidOperationException("A manutenção já acabou.");
        }

        // O banco guarda microssegundos: o fim novo é o agora sem a fração menor, e a comparação usa o mesmo valor,
        // para o fim nunca igualar o início (CHECK fim > inicio).
        var corte = Truncar(agora);
        if (Inicio < corte)
        {
            Fim = corte;
        }
        else
        {
            Situacao = SituacaoDaManutencao.Cancelada;
        }

        EncerradaEm = agora;
        EncerradaPorId = encerradaPorId;
    }

    private static DateTimeOffset Truncar(DateTimeOffset instante) =>
        new(instante.UtcTicks - (instante.UtcTicks % (TimeSpan.TicksPerMillisecond / 1000)), TimeSpan.Zero);
}
