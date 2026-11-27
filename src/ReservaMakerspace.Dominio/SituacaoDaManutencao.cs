namespace ReservaMakerspace.Dominio;

/// <summary>
/// Situação de uma manutenção. A que acabou ou foi encurtada continua <c>Ativa</c> (o fim é que mudou); só a que
/// ainda não tinha começado e foi encerrada vira <c>Cancelada</c>.
/// </summary>
public enum SituacaoDaManutencao
{
    Ativa,
    Cancelada,
}
