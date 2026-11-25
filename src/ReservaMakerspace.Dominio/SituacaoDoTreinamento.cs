namespace ReservaMakerspace.Dominio;

/// <summary>Situação de um treinamento num instante. É calculada na consulta, nunca gravada (plano do 1.4, decisão 4).</summary>
public enum SituacaoDoTreinamento
{
    Valido,
    Vencido,
    Revogado,
}
