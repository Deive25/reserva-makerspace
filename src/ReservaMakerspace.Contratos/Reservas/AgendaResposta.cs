using ReservaMakerspace.Contratos.Manutencoes;

namespace ReservaMakerspace.Contratos.Reservas;

/// <summary>
/// A agenda de uma máquina na janela <c>[De, Ate)</c> (<c>GET /equipamentos/{id}/agenda</c>), com os horários em UTC.
/// <c>Manutencoes</c> traz as manutenções ativas que cruzam a janela, em ordem de início (plano do 1.7, decisão 10).
/// </summary>
public record AgendaResposta(
    Guid EquipamentoId,
    string EquipamentoNome,
    DateTimeOffset De,
    DateTimeOffset Ate,
    List<ReservaNaAgenda> Reservas,
    List<ManutencaoNaAgenda> Manutencoes);
