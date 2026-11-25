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
/// Máquinas reserváveis. Os GET valem para qualquer pessoa logada e devolvem também as inativas
/// (a agenda precisa mostrá-las); o POST e o PUT são da Coordenação. Não há DELETE (spec §4.6).
/// </summary>
[ApiController]
[Route("equipamentos")]
public class EquipamentosController(MakerspaceDbContext banco) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<List<EquipamentoResposta>>(StatusCodes.Status200OK)]
    public async Task<List<EquipamentoResposta>> Listar() =>
        await banco.Equipamentos.AsNoTracking()
            .OrderBy(e => e.Tipo.Nome).ThenBy(e => e.Nome).ThenBy(e => e.Id)
            .Select(e => new EquipamentoResposta(e.Id, e.Nome, e.TipoId, e.Tipo.Nome, e.Ativo))
            .ToListAsync();

    [HttpGet("{id:guid}")]
    [ProducesResponseType<EquipamentoResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Buscar(Guid id)
    {
        var maquina = await banco.Equipamentos.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new EquipamentoResposta(e.Id, e.Nome, e.TipoId, e.Tipo.Nome, e.Ativo))
            .FirstOrDefaultAsync();
        return maquina is null ? NaoEncontrado() : Ok(maquina);
    }

    [HttpPost]
    [Authorize(Policy = Politicas.Coordenacao)]
    [ProducesResponseType<EquipamentoResposta>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Criar(PedidoDeEquipamento pedido)
    {
        var tipo = await BuscarTipoAsync(pedido.TipoId!.Value);
        if (tipo is null)
        {
            return TipoInexistente();
        }

        var maquina = new Equipamento
        {
            Nome = pedido.Nome.Trim(),
            TipoId = tipo.Id,
            Tipo = tipo,
            Ativo = pedido.Ativo!.Value,
        };
        banco.Equipamentos.Add(maquina);

        if (await GravarAsync() is { } conflito)
        {
            return conflito;
        }

        return Created($"/equipamentos/{maquina.Id}", Resposta(maquina));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Politicas.Coordenacao)]
    [ProducesResponseType<EquipamentoResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Alterar(Guid id, PedidoDeEquipamento pedido)
    {
        var maquina = await banco.Equipamentos.FirstOrDefaultAsync(e => e.Id == id);
        if (maquina is null)
        {
            return NaoEncontrado();
        }

        var tipo = await BuscarTipoAsync(pedido.TipoId!.Value);
        if (tipo is null)
        {
            return TipoInexistente();
        }

        maquina.Nome = pedido.Nome.Trim();
        maquina.Tipo = tipo;
        maquina.Ativo = pedido.Ativo!.Value;

        if (await GravarAsync() is { } conflito)
        {
            return conflito;
        }

        return Ok(Resposta(maquina));
    }

    private Task<TipoDeEquipamento?> BuscarTipoAsync(Guid tipoId) =>
        banco.TiposDeEquipamento.FirstOrDefaultAsync(t => t.Id == tipoId);

    /// <summary>Um <c>tipoId</c> do corpo que não existe é erro de validação (400); o 404 fica para o id da URL.</summary>
    private ActionResult TipoInexistente()
    {
        ModelState.AddModelError("tipoId", "Tipo de equipamento não encontrado.");
        return ValidationProblem(ModelState);
    }

    /// <summary>Grava e traduz a violação do índice único do nome em 409. Devolve <c>null</c> se gravou.</summary>
    private async Task<IActionResult?> GravarAsync()
    {
        try
        {
            await banco.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateException erro) when (ViolacaoDeUnicidade.Eh(erro, MakerspaceDbContext.IndiceDoNomeDaMaquina))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: TitulosDeErro.Para(StatusCodes.Status409Conflict),
                detail: "Já existe uma máquina com este nome.");
        }
    }

    private ObjectResult NaoEncontrado() =>
        Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: TitulosDeErro.Para(StatusCodes.Status404NotFound),
            detail: "Máquina não encontrada.");

    private static EquipamentoResposta Resposta(Equipamento e) => new(e.Id, e.Nome, e.TipoId, e.Tipo.Nome, e.Ativo);
}
