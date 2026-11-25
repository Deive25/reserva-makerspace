using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReservaMakerspace.Api.Autenticacao;
using ReservaMakerspace.Api.Dados;
using ReservaMakerspace.Api.Erros;
using ReservaMakerspace.Contratos.Contas;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Api.Controllers;

[ApiController]
[Authorize(Policy = Politicas.Coordenacao)]
[Route("usuarios")]
public class UsuariosController(MakerspaceDbContext banco) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<List<UsuarioResposta>>(StatusCodes.Status200OK)]
    public async Task<List<UsuarioResposta>> Listar() =>
        await banco.Users.AsNoTracking()
            .OrderBy(u => u.Nome).ThenBy(u => u.Id)
            .Select(u => new UsuarioResposta(u.Id, u.Nome, u.Email!, u.Papel))
            .ToListAsync();

    [HttpPut("{id:guid}/papel")]
    [ProducesResponseType<UsuarioResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DefinirPapel(Guid id, PedidoDePapel pedido)
    {
        var novoPapel = pedido.Papel!.Value;

        // A transação trava as linhas das Coordenações, em ordem de id, antes de contar.
        // Sem a trava, duas Coordenações rebaixando uma à outra ao mesmo tempo
        // deixariam o parque sem nenhuma (ADR 0003).
        await using var transacao = await banco.Database.BeginTransactionAsync();
        var coordenacoes = await banco.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM usuarios WHERE papel = 'Coordenacao' ORDER BY id FOR UPDATE""")
            .ToListAsync();

        // Lido depois da trava, para ver o estado já confirmado por quem veio antes.
        var usuario = await banco.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (usuario is null)
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: TitulosDeErro.Para(StatusCodes.Status404NotFound),
                detail: "Usuário não encontrado.");
        }

        if (usuario.Papel == Papel.Coordenacao && novoPapel != Papel.Coordenacao && coordenacoes.Count <= 1)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: TitulosDeErro.Para(StatusCodes.Status409Conflict),
                detail: "A última Coordenação não pode perder o papel.");
        }

        usuario.Papel = novoPapel;
        await banco.SaveChangesAsync();
        await transacao.CommitAsync();
        return Ok(AuthController.Resposta(usuario));
    }
}
