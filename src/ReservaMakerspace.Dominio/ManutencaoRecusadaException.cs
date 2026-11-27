namespace ReservaMakerspace.Dominio;

/// <summary>Uma regra de negócio do registro de manutenção foi violada. A API a devolve como 422.</summary>
public sealed class ManutencaoRecusadaException(string mensagem) : Exception(mensagem);
