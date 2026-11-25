using System.ComponentModel.DataAnnotations;

namespace ReservaMakerspace.Contratos.Contas;

/// <summary>Dados do cadastro público. Não tem campo de papel: quem se cadastra sempre vira Usuário.</summary>
public class PedidoDeCadastro
{
    [Required(ErrorMessage = "Informe o nome.")]
    [StringLength(100, ErrorMessage = "O nome pode ter até 100 caracteres.")]
    public string Nome { get; set; } = "";

    [Required(ErrorMessage = "Informe o e-mail.")]
    [EmailAddress(ErrorMessage = "Informe um e-mail válido.")]
    [StringLength(256, ErrorMessage = "O e-mail pode ter até 256 caracteres.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Informe a senha.")]
    [StringLength(128, MinimumLength = 15, ErrorMessage = "A senha precisa ter pelo menos 15 caracteres.")]
    public string Senha { get; set; } = "";
}
