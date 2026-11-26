namespace ReservaMakerspace.Contratos.Reservas;

/// <summary>
/// A agenda de uma máquina na janela <c>[De, Ate)</c> (<c>GET /equipamentos/{id}/agenda</c>), com os horários em UTC.
/// Um objeto, e não uma lista, para o 1.7 acrescentar <c>manutencoes</c> sem mudar o que já existe.
/// </summary>
public record AgendaResposta(
    Guid EquipamentoId,
    string EquipamentoNome,
    DateTimeOffset De,
    DateTimeOffset Ate,
    List<ReservaNaAgenda> Reservas);
