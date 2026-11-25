using System.ComponentModel.DataAnnotations;

namespace ReservaMakerspace.Contratos.Treinamentos;

/// <summary>Corpo do POST /treinamentos/{id}/revogacao. O <c>Required</c> também recusa o motivo só com espaços.</summary>
public class PedidoDeRevogacao
{
    [Required(ErrorMessage = "Informe o motivo.")]
    [StringLength(500, ErrorMessage = "O motivo pode ter até 500 caracteres.")]
    public string Motivo { get; set; } = "";
}
