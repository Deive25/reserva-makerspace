using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ReservaMakerspace.Api.Dados;
using ReservaMakerspace.Contratos.Contas;

namespace ReservaMakerspace.Api.Controllers;

[ApiController]
[Route("me")]
public class MeController(UserManager<Usuario> usuarios) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<UsuarioResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Obter()
    {
        // O papel já foi relido do banco na validação do token; aqui só lemos a conta inteira.
        var usuario = await usuarios.FindByIdAsync(User.FindFirstValue("sub")!);
        return usuario is null ? Unauthorized() : Ok(AuthController.Resposta(usuario));
    }
}
