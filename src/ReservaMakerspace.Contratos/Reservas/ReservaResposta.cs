using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Contratos.Reservas;

/// <summary>
/// Uma reserva com os nomes já resolvidos. Os horários saem em UTC. Os campos do cancelamento só vêm
/// preenchidos quando a situação não é <c>Ativa</c>; o motivo só existe no cancelamento da Equipe.
/// </summary>
public record ReservaResposta(
    Guid Id,
    Guid EquipamentoId,
    string EquipamentoNome,
    string TipoNome,
    Guid OperadorId,
    string OperadorNome,
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    FinalidadeDaReserva Finalidade,
    string? Solicitante,
    string? Descricao,
    SituacaoDaReserva Situacao,
    DateTimeOffset CriadaEm,
    DateTimeOffset? CanceladaEm,
    string? CanceladaPorNome,
    string? MotivoDoCancelamento);
