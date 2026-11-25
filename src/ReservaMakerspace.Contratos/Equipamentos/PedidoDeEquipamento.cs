using System.ComponentModel.DataAnnotations;

namespace ReservaMakerspace.Contratos.Equipamentos;

/// <summary>
/// Corpo do POST e do PUT de máquina. O PUT substitui o recurso inteiro, inclusive <see cref="Ativo"/>:
/// se ele fosse opcional, um PUT feito só para renomear reativaria sem querer uma máquina inativa.
/// </summary>
public class PedidoDeEquipamento
{
    [Required(ErrorMessage = "Informe o nome.")]
    [StringLength(100, ErrorMessage = "O nome pode ter até 100 caracteres.")]
    public string Nome { get; set; } = "";

    [Required(ErrorMessage = "Informe o tipo de equipamento.")]
    public Guid? TipoId { get; set; }

    [Required(ErrorMessage = "Informe se a máquina está ativa.")]
    public bool? Ativo { get; set; }
}
