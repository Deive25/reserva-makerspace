using System.ComponentModel.DataAnnotations;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Contratos.Reservas;

/// <summary>
/// Corpo do POST /reservas. Não há <c>operadorId</c>: o operador é sempre quem faz o pedido (spec §4.1).
/// Os horários precisam trazer o fuso (<c>Z</c> ou <c>-03:00</c>); sem ele a API responde 400 (plano do 1.5, decisão 11).
/// </summary>
public class PedidoDeReserva
{
    [Required(ErrorMessage = "Informe a máquina.")]
    public Guid? EquipamentoId { get; set; }

    [Required(ErrorMessage = "Informe o início.")]
    public DateTimeOffset? Inicio { get; set; }

    [Required(ErrorMessage = "Informe o fim.")]
    public DateTimeOffset? Fim { get; set; }

    [Required(ErrorMessage = "Informe a finalidade.")]
    public FinalidadeDaReserva? Finalidade { get; set; }

    /// <summary>Quem pediu ao bolsista. Só na demanda do parque.</summary>
    [StringLength(100, ErrorMessage = "O solicitante pode ter até 100 caracteres.")]
    public string? Solicitante { get; set; }

    [StringLength(500, ErrorMessage = "A descrição pode ter até 500 caracteres.")]
    public string? Descricao { get; set; }
}
