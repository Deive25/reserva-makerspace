namespace ReservaMakerspace.Dominio;

/// <summary>Uma reserva ativa que cruza o horário da demanda, com o que as travas da desmarcação precisam saber.</summary>
public sealed record ReservaEmConflito(DateTimeOffset Inicio, FinalidadeDaReserva Finalidade);

/// <summary>Qual trava da §4.3 da spec recusou a desmarcação.</summary>
public enum TravaDaDesmarcacao
{
    /// <summary>Sem motivo, o conflito volta como está, para a tela perguntar o motivo (trava 4).</summary>
    MotivoObrigatorio,

    /// <summary>Uma manutenção nunca se desmarca (trava 3): a máquina está parada, qualquer que seja a reserva em conflito.</summary>
    ManutencaoNaoSeDesmarca,

    /// <summary>Uma demanda do parque nunca desmarca outra (trava 3).</summary>
    DemandaNaoSeDesmarca,

    /// <summary>Só se desmarca quem começa daqui a mais de 24 h (trava 1).</summary>
    AntecedenciaMinima,

    /// <summary>Há outra máquina do tipo livre: reserve-a em vez de desmarcar (trava 2).</summary>
    HaMaquinaLivre,
}

/// <summary>Por que a desmarcação foi recusada. A mensagem é a que a API devolve no <c>detail</c> do 409.</summary>
public sealed record RecusaDaDesmarcacao(TravaDaDesmarcacao Trava, string Mensagem);

/// <summary>
/// As travas da desmarcação de reservas pessoais para uma demanda do parque (spec §4.3). Só decide: quem lê o
/// banco e cancela é a API, sob a trava da máquina. O "agora" chega por parâmetro (vem do <see cref="TimeProvider"/>).
/// </summary>
public static class Desmarcacao
{
    public const int HorasMinimasDeAntecedencia = 24;

    /// <summary>A mensagem do 409 simples, a mesma de uma reserva que só bate num horário ocupado.</summary>
    public const string MensagemDoConflito = "A máquina já tem uma reserva ativa nesse horário.";

    /// <summary>A mensagem de todo 409 de agenda em que há uma manutenção no intervalo (plano do 1.7, decisão 7).</summary>
    public const string MensagemDaManutencao = "A máquina está em manutenção nesse horário.";

    /// <summary>
    /// Confere as travas na ordem do plano do 1.6 (decisão 5; o 1.7 põe a manutenção na trava 3): o motivo (trava 4); depois as duas proibições, que
    /// nenhuma outra máquina resolve (trava 3 e trava 1); por último a que tem saída (trava 2). Os limites são
    /// estritos, como diz a spec: "mais de 24 h" (<c>inicio &lt;= agora + 24 h</c> recusa).
    /// </summary>
    /// <param name="conflitos">As reservas ativas que cruzam o horário da demanda (vazia se só há manutenção).</param>
    /// <param name="motivo">O motivo digitado; nulo ou só espaços conta como ausente.</param>
    /// <param name="nomeDaMaquinaLivre">O nome de outra máquina ativa do tipo livre no intervalo inteiro, ou <c>null</c> se não há.</param>
    /// <param name="agora">O instante do <see cref="TimeProvider"/>.</param>
    /// <param name="haManutencao">Há uma manutenção ativa no intervalo da demanda. Com ela, o 409 usa a mensagem da manutenção e,
    /// com o motivo, a manutenção recusa antes de qualquer outra trava.</param>
    /// <returns>A recusa, ou <c>null</c> se todas as travas passaram.</returns>
    public static RecusaDaDesmarcacao? Avaliar(
        IReadOnlyCollection<ReservaEmConflito> conflitos,
        string? motivo,
        string? nomeDaMaquinaLivre,
        DateTimeOffset agora,
        bool haManutencao = false)
    {
        // Trava 4.
        if (Reserva.Normalizar(motivo) is null)
        {
            return new RecusaDaDesmarcacao(
                TravaDaDesmarcacao.MotivoObrigatorio, haManutencao ? MensagemDaManutencao : MensagemDoConflito);
        }

        // Trava 3: a manutenção vem antes da demanda, porque a máquina está parada qualquer que seja a reserva em conflito.
        if (haManutencao)
        {
            return new RecusaDaDesmarcacao(TravaDaDesmarcacao.ManutencaoNaoSeDesmarca, "Uma manutenção não se desmarca.");
        }

        if (conflitos.Any(c => c.Finalidade == FinalidadeDaReserva.DemandaDoParque))
        {
            return new RecusaDaDesmarcacao(
                TravaDaDesmarcacao.DemandaNaoSeDesmarca, "Uma demanda do parque não desmarca outra.");
        }

        // Trava 1: uma reserva em andamento (início no passado) cai aqui também.
        var limite = agora.AddHours(HorasMinimasDeAntecedencia);
        if (conflitos.Any(c => c.Inicio <= limite))
        {
            return new RecusaDaDesmarcacao(
                TravaDaDesmarcacao.AntecedenciaMinima,
                $"Só dá para desmarcar reservas que começam daqui a mais de {HorasMinimasDeAntecedencia} h.");
        }

        // Trava 2.
        if (nomeDaMaquinaLivre is not null)
        {
            return new RecusaDaDesmarcacao(
                TravaDaDesmarcacao.HaMaquinaLivre,
                $"A {nomeDaMaquinaLivre} está livre nesse horário. Reserve-a em vez de desmarcar.");
        }

        return null;
    }
}
