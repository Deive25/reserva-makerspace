using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReservaMakerspace.Api.Autenticacao;
using ReservaMakerspace.Api.Dados;
using ReservaMakerspace.Api.Erros;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Api.Controllers;

/// <summary>
/// Máquinas reserváveis. Os GET valem para qualquer pessoa logada e devolvem também as inativas
/// (a agenda precisa mostrá-las); o POST e o PUT são da Coordenação. Não há DELETE (spec §4.6).
/// </summary>
[ApiController]
[Route("equipamentos")]
public class EquipamentosController(MakerspaceDbContext banco, TimeProvider relogio) : ControllerBase
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
        // Trava a linha da máquina antes de conferir as reservas, do mesmo jeito que a criação da reserva (FOR NO KEY
        // UPDATE, até o commit). Assim o PUT não cruza com uma reserva sendo criada: ou ela já foi confirmada e é vista
        // abaixo, ou espera este PUT e depois recusa a máquina inativa (ADR 0006).
        await using var transacao = await banco.Database.BeginTransactionAsync();
        var achou = await banco.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM equipamentos WHERE id = {id} FOR NO KEY UPDATE""")
            .ToListAsync();
        if (achou.Count == 0)
        {
            return NaoEncontrado();
        }

        var maquina = await banco.Equipamentos.Include(e => e.Tipo).FirstAsync(e => e.Id == id);
        var tipo = await BuscarTipoAsync(pedido.TipoId!.Value);
        if (tipo is null)
        {
            return TipoInexistente();
        }

        // Desativar ou trocar o tipo com reservas ativas e futuras é recusado (spec §4.6). Renomear e reativar,
        // não. Para tirar a máquina de uso na hora, registra-se uma manutenção (1.7).
        var desativando = maquina.Ativo && !pedido.Ativo!.Value;
        var trocandoDeTipo = maquina.TipoId != tipo.Id;
        if (desativando || trocandoDeTipo)
        {
            var agora = relogio.GetUtcNow();
            var reservas = await banco.Reservas.AsNoTracking()
                .Where(r => r.EquipamentoId == id && r.Situacao == SituacaoDaReserva.Ativa && r.Fim > agora)
                .OrderBy(r => r.Inicio).ThenBy(r => r.Id)
                .Select(r => new ConflitoDeAgenda(r.Inicio, r.Fim, r.Finalidade))
                .ToListAsync();
            if (reservas.Count > 0)
            {
                var problema = ProblemDetailsFactory.CreateProblemDetails(
                    HttpContext,
                    StatusCodes.Status409Conflict,
                    title: TitulosDeErro.Para(StatusCodes.Status409Conflict),
                    detail: $"A máquina tem reservas ativas que ainda não acabaram. Cancele-as antes de {(desativando ? "desativá-la" : "trocar o tipo")}.");
                problema.Extensions["conflitos"] = reservas;
                return new ObjectResult(problema) { StatusCode = StatusCodes.Status409Conflict };
            }
        }

        maquina.Nome = pedido.Nome.Trim();
        maquina.Tipo = tipo;
        maquina.Ativo = pedido.Ativo!.Value;

        if (await GravarAsync() is { } conflito)
        {
            return conflito;
        }

        await transacao.CommitAsync();
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
