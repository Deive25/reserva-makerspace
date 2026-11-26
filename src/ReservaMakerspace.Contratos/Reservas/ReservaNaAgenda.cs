using System.Text.Json.Serialization;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Contratos.Reservas;

/// <summary>
/// Uma reserva ativa na agenda de uma máquina: um item da agenda e, com a mesma forma, da lista <c>conflitos</c> dos
/// 409 (plano do 1.6, decisão 3). <c>ReservaId</c>, <c>OperadorNome</c> e <c>Solicitante</c> vêm preenchidos para a
/// Equipe e a Coordenação. Para o Usuário, só nas reservas dele; nas dos outros ficam de fora do JSON, e ninguém é
/// exposto. A descrição não entra aqui (a Equipe a vê em <c>GET /reservas/{id}</c>).
/// </summary>
public record ReservaNaAgenda(
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    FinalidadeDaReserva Finalidade,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ReservaId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OperadorNome = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Solicitante = null);
