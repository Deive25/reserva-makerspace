using System.ComponentModel.DataAnnotations;

namespace ReservaMakerspace.Contratos.Treinamentos;

/// <summary>
/// Corpo do POST /treinamentos. Não há data: o treinamento vale a partir do momento do registro
/// (plano do 1.4, decisão 2).
/// </summary>
public class PedidoDeTreinamento
{
    [Required(ErrorMessage = "Informe a pessoa.")]
    public Guid? UsuarioId { get; set; }

    [Required(ErrorMessage = "Informe o tipo de equipamento.")]
    public Guid? TipoId { get; set; }
}
