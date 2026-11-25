using System.ComponentModel.DataAnnotations;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Contratos.Contas;

public class PedidoDePapel
{
    [Required(ErrorMessage = "Informe o papel.")]
    public Papel? Papel { get; set; }
}
