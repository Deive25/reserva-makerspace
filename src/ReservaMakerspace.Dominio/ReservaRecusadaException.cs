namespace ReservaMakerspace.Dominio;

/// <summary>Por que a criação de uma reserva foi recusada; a API traduz em 403 ou 422.</summary>
public enum MotivoDaRecusa
{
    /// <summary>O papel da pessoa não permite o pedido (spec §4.2, regra 7).</summary>
    SemPermissao,

    /// <summary>Uma regra de negócio da §4.2 não foi cumprida.</summary>
    RegraDeNegocio,
}

/// <summary>Uma regra da §4.2 foi violada. A mensagem é a que a API devolve à pessoa.</summary>
public sealed class ReservaRecusadaException(MotivoDaRecusa motivo, string mensagem) : Exception(mensagem)
{
    public MotivoDaRecusa Motivo { get; } = motivo;
}
