using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Contratos.Reservas;

/// <summary>
/// Uma reserva ativa que cruza o horário pedido, na lista <c>conflitos</c> do 409. Sem o nome do operador,
/// como a agenda mostra para quem não é da Equipe.
/// </summary>
public record ConflitoDeAgenda(DateTimeOffset Inicio, DateTimeOffset Fim, FinalidadeDaReserva Finalidade);
