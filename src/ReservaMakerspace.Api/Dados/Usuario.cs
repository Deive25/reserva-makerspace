using Microsoft.AspNetCore.Identity;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Api.Dados;

/// <summary>Conta de uma pessoa. O <c>UserName</c> é o e-mail, e o papel é um campo da própria conta.</summary>
public class Usuario : IdentityUser<Guid>
{
    public string Nome { get; set; } = "";

    public Papel Papel { get; set; } = Papel.Usuario;
}
