using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReservaMakerspace.Api.Autenticacao;
using ReservaMakerspace.Api.Dados;
using ReservaMakerspace.Api.Erros;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Api.Controllers;

/// <summary>
/// Os GET valem para qualquer pessoa logada (a política padrão). O POST e o PUT são da Coordenação.
/// Não há DELETE: a spec troca a exclusão pela desativação (§4.6).
/// </summary>
[ApiController]
[Route("tipos-de-equipamento")]
public class TiposDeEquipamentoController(MakerspaceDbContext banco) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<List<TipoDeEquipamentoResposta>>(StatusCodes.Status200OK)]
    public async Task<List<TipoDeEquipamentoResposta>> Listar() =>
        (await banco.TiposDeEquipamento.AsNoTracking()
            .OrderBy(t => t.Nome).ThenBy(t => t.Id)
            .ToListAsync())
        .Select(Resposta)
        .ToList();

    [HttpGet("{id:guid}")]
    [ProducesResponseType<TipoDeEquipamentoResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Buscar(Guid id)
    {
        var tipo = await banco.TiposDeEquipamento.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        return tipo is null ? NaoEncontrado() : Ok(Resposta(tipo));
    }

    [HttpPost]
    [Authorize(Policy = Politicas.Coordenacao)]
    [ProducesResponseType<TipoDeEquipamentoResposta>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Criar(PedidoDeTipoDeEquipamento pedido)
    {
        var tipo = new TipoDeEquipamento
        {
            Nome = pedido.Nome.Trim(),
            DuracaoMaximaPessoal = TimeSpan.FromMinutes(pedido.DuracaoMaximaPessoalMinutos!.Value),
        };
        banco.TiposDeEquipamento.Add(tipo);

        // Sem consultar antes: quem decide é o índice único, então não há janela entre conferir e gravar.
        if (await GravarAsync() is { } conflito)
        {
            return conflito;
        }

        return Created($"/tipos-de-equipamento/{tipo.Id}", Resposta(tipo));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Politicas.Coordenacao)]
    [ProducesResponseType<TipoDeEquipamentoResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Alterar(Guid id, PedidoDeTipoDeEquipamento pedido)
    {
        var tipo = await banco.TiposDeEquipamento.FirstOrDefaultAsync(t => t.Id == id);
        if (tipo is null)
        {
            return NaoEncontrado();
        }

        tipo.Nome = pedido.Nome.Trim();
        tipo.DuracaoMaximaPessoal = TimeSpan.FromMinutes(pedido.DuracaoMaximaPessoalMinutos!.Value);

        if (await GravarAsync() is { } conflito)
        {
            return conflito;
        }

        return Ok(Resposta(tipo));
    }

    /// <summary>Grava e traduz a violação do índice único do nome em 409. Devolve <c>null</c> se gravou.</summary>
    private async Task<IActionResult?> GravarAsync()
    {
        try
        {
            await banco.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateException erro) when (ViolacaoDeUnicidade.Eh(erro, MakerspaceDbContext.IndiceDoNomeDoTipo))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: TitulosDeErro.Para(StatusCodes.Status409Conflict),
                detail: "Já existe um tipo com este nome.");
        }
    }

    private ObjectResult NaoEncontrado() =>
        Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: TitulosDeErro.Para(StatusCodes.Status404NotFound),
            detail: "Tipo de equipamento não encontrado.");

    private static TipoDeEquipamentoResposta Resposta(TipoDeEquipamento t) =>
        new(t.Id, t.Nome, (int)t.DuracaoMaximaPessoal.TotalMinutes);
}
