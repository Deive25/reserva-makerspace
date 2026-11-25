using System.ComponentModel.DataAnnotations;

namespace ReservaMakerspace.Contratos.Equipamentos;

/// <summary>Corpo do POST e do PUT de tipo. O PUT substitui o recurso inteiro: todos os campos são obrigatórios.</summary>
public class PedidoDeTipoDeEquipamento
{
    [Required(ErrorMessage = "Informe o nome.")]
    [StringLength(100, ErrorMessage = "O nome pode ter até 100 caracteres.")]
    public string Nome { get; set; } = "";

    /// <summary>Em minutos, de 15 (15 min) a 10 080 (7 dias). Um inteiro é fácil de validar e de pôr num formulário.</summary>
    [Required(ErrorMessage = "Informe a duração máxima.")]
    [Range(15, 10_080, ErrorMessage = "A duração máxima precisa ficar entre 15 minutos e 7 dias.")]
    public int? DuracaoMaximaPessoalMinutos { get; set; }
}
