using System.ComponentModel.DataAnnotations;

namespace ReservaMakerspace.Contratos.Manutencoes;

/// <summary>
/// Corpo do POST /equipamentos/{id}/manutencoes. Os horários precisam trazer o fuso (<c>Z</c> ou <c>-03:00</c>); sem ele
/// a API responde 400. Sem <c>inicio</c>, a manutenção começa agora ("parar a máquina agora", plano do 1.7, decisão 4).
/// O <c>Required</c> do motivo também recusa o texto só com espaços.
/// </summary>
public class PedidoDeManutencao
{
    /// <summary>Opcional. Com ele, não pode ser no passado.</summary>
    public DateTimeOffset? Inicio { get; set; }

    [Required(ErrorMessage = "Informe o fim.")]
    public DateTimeOffset? Fim { get; set; }

    [Required(ErrorMessage = "Informe o motivo.")]
    [StringLength(500, ErrorMessage = "O motivo pode ter até 500 caracteres.")]
    public string Motivo { get; set; } = "";
}
