namespace ReservaMakerspace.Dominio;

/// <summary>
/// Situação de uma reserva. <c>CanceladaPorManutencao</c> (spec §4.1) entra no 1.7: como o valor é guardado
/// como texto, acrescentá-lo não exige migração.
/// </summary>
public enum SituacaoDaReserva
{
    Ativa,
    CanceladaPeloDono,
    CanceladaPelaEquipe,
    CanceladaPorManutencao,
}
