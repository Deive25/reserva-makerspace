using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ReservaMakerspace.Api.Autenticacao;
using ReservaMakerspace.Api.Dados;
using ReservaMakerspace.Api.Erros;
using ReservaMakerspace.Contratos.Contas;

namespace ReservaMakerspace.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("auth")]
public class AuthController(UserManager<Usuario> usuarios, EmissorDeToken emissor) : ControllerBase
{
    private const string MensagemDeLoginInvalido = "E-mail ou senha incorretos.";

    /// <summary>Cadastro público: sempre cria um Usuário (o pedido não tem campo de papel).</summary>
    [HttpPost("cadastro")]
    [ProducesResponseType<UsuarioResposta>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Cadastrar(PedidoDeCadastro pedido)
    {
        var email = pedido.Email.Trim();
        var usuario = new Usuario { UserName = email, Email = email, Nome = pedido.Nome.Trim() };

        var resultado = await usuarios.CreateAsync(usuario, pedido.Senha);
        if (resultado.Succeeded)
        {
            return Created("/me", Resposta(usuario));
        }

        if (resultado.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail)
                                          or nameof(IdentityErrorDescriber.DuplicateUserName)))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: TitulosDeErro.Para(StatusCodes.Status409Conflict),
                detail: "Já existe uma conta com este e-mail.");
        }

        foreach (var erro in resultado.Errors)
        {
            ModelState.AddModelError(CampoDoErro(erro.Code), erro.Description);
        }

        return ValidationProblem(ModelState);
    }

    /// <summary>E-mail inexistente e senha errada dão a mesma resposta, para não revelar quais e-mails têm conta.</summary>
    [HttpPost("login")]
    [ProducesResponseType<RespostaDeLogin>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Entrar(PedidoDeLogin pedido)
    {
        var usuario = await usuarios.FindByEmailAsync(pedido.Email.Trim());
        if (usuario is null || !await usuarios.CheckPasswordAsync(usuario, pedido.Senha))
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: TitulosDeErro.Para(StatusCodes.Status401Unauthorized),
                detail: MensagemDeLoginInvalido);
        }

        var (token, expiraEm) = emissor.Emitir(usuario);
        return Ok(new RespostaDeLogin(token, expiraEm, Resposta(usuario)));
    }

    internal static UsuarioResposta Resposta(Usuario u) => new(u.Id, u.Nome, u.Email!, u.Papel);

    private static string CampoDoErro(string codigo) => codigo switch
    {
        var c when c.StartsWith("Password", StringComparison.Ordinal) => "senha",
        var c when c.Contains("Email", StringComparison.Ordinal) => "email",
        var c when c.Contains("UserName", StringComparison.Ordinal) => "email",
        _ => "",
    };
}
