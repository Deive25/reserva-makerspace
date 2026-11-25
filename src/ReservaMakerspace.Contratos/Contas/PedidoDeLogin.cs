using System.ComponentModel.DataAnnotations;

namespace ReservaMakerspace.Contratos.Contas;

public class PedidoDeLogin
{
    [Required(ErrorMessage = "Informe o e-mail.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Informe a senha.")]
    public string Senha { get; set; } = "";
}
