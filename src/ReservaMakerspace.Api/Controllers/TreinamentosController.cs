using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReservaMakerspace.Api.Autenticacao;
using ReservaMakerspace.Api.Dados;
using ReservaMakerspace.Api.Erros;
using ReservaMakerspace.Contratos.Treinamentos;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Api.Controllers;

/// <summary>
/// Treinamentos (spec §4.1, §4.5 e §4.6). As rotas de <c>/treinamentos</c> são da Equipe (e da Coordenação);
/// <c>GET /me/treinamentos</c> é de qualquer pessoa logada e fica aqui para a consulta e a resposta
/// ficarem num lugar só. Não há PUT nem DELETE: o histórico não se apaga, e revogar é a única mudança.
/// A situação (válido, vencido, revogado) sai do <see cref="TimeProvider"/> da API, e nunca do banco.
/// </summary>
[ApiController]
[Route("treinamentos")]
public class TreinamentosController(MakerspaceDbContext banco, TimeProvider relogio) : ControllerBase
{
    private const string MotivoDoCancelamentoPorRevogacao = "Treinamento revogado";

    /// <summary>O treinamento e os nomes que a resposta traz, antes de calcular a situação.</summary>
    // Classe com inicializador de objeto (e não record com construtor): o EF Core só consegue acrescentar
    // Where e OrderBy depois do select quando a projeção é desse tipo.
    private sealed class Linha
    {
        public required Treinamento Treinamento { get; init; }

        public required string UsuarioNome { get; init; }

        public required string TipoNome { get; init; }

        public required string RegistradoPorNome { get; init; }

        public string? RevogadoPorNome { get; init; }
    }

    [HttpGet]
    [Authorize(Policy = Politicas.Equipe)]
    [ProducesResponseType<List<TreinamentoResposta>>(StatusCodes.Status200OK)]
    public async Task<List<TreinamentoResposta>> Listar([FromQuery] Guid? usuarioId)
    {
        var consulta = Consultar();
        if (usuarioId is not null)
        {
            consulta = consulta.Where(l => l.Treinamento.UsuarioId == usuarioId);
        }

        var linhas = await consulta
            .OrderBy(l => l.UsuarioNome).ThenBy(l => l.TipoNome)
            .ThenByDescending(l => l.Treinamento.RealizadoEm).ThenBy(l => l.Treinamento.Id)
            .ToListAsync();
        return linhas.Select(Resposta).ToList();
    }

    [HttpGet("/me/treinamentos")]
    [ProducesResponseType<List<TreinamentoResposta>>(StatusCodes.Status200OK)]
    public async Task<List<TreinamentoResposta>> ListarMeus()
    {
        var eu = Guid.Parse(User.FindFirstValue("sub")!);
        var linhas = await Consultar()
            .Where(l => l.Treinamento.UsuarioId == eu)
            .OrderBy(l => l.TipoNome)
            .ThenByDescending(l => l.Treinamento.RealizadoEm).ThenBy(l => l.Treinamento.Id)
            .ToListAsync();
        return linhas.Select(Resposta).ToList();
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Politicas.Equipe)]
    [ProducesResponseType<TreinamentoResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Buscar(Guid id)
    {
        var linha = await Consultar().FirstOrDefaultAsync(l => l.Treinamento.Id == id);
        return linha is null ? NaoEncontrado() : Ok(Resposta(linha));
    }

    [HttpPost]
    [Authorize(Policy = Politicas.Equipe)]
    [ProducesResponseType<TreinamentoResposta>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Registrar(PedidoDeTreinamento pedido)
    {
        var usuarioId = pedido.UsuarioId!.Value;
        var tipoId = pedido.TipoId!.Value;
        var quemRegistra = Guid.Parse(User.FindFirstValue("sub")!);

        // A Equipe é treinada por outra pessoa da Equipe ou pela Coordenação (plano do 1.4, decisão 8).
        if (usuarioId == quemRegistra)
        {
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: TitulosDeErro.Para(StatusCodes.Status422UnprocessableEntity),
                detail: "Ninguém pode registrar o próprio treinamento.");
        }

        // Um id inexistente no corpo é erro de validação (400); o 404 fica para o id da URL.
        if (!await banco.Users.AnyAsync(u => u.Id == usuarioId))
        {
            ModelState.AddModelError("usuarioId", "Pessoa não encontrada.");
        }

        if (!await banco.TiposDeEquipamento.AnyAsync(t => t.Id == tipoId))
        {
            ModelState.AddModelError("tipoId", "Tipo de equipamento não encontrado.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        // Renovar é registrar outro: não se confere se a pessoa já tem um válido (decisão 5).
        var treinamento = Treinamento.Registrar(usuarioId, tipoId, quemRegistra, relogio.GetUtcNow());
        banco.Treinamentos.Add(treinamento);
        await banco.SaveChangesAsync();

        var linha = await Consultar().FirstAsync(l => l.Treinamento.Id == treinamento.Id);
        return Created($"/treinamentos/{treinamento.Id}", Resposta(linha));
    }

    [HttpPost("{id:guid}/revogacao")]
    [Authorize(Policy = Politicas.Equipe)]
    [ProducesResponseType<TreinamentoResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Revogar(Guid id, PedidoDeRevogacao pedido)
    {
        // A pessoa e o tipo nunca mudam; por isso podem ser lidos antes da trava.
        var alvo = await banco.Treinamentos.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.UsuarioId, t.TipoId })
            .FirstOrDefaultAsync();
        if (alvo is null)
        {
            return NaoEncontrado();
        }

        // A transação trava as linhas da pessoa no tipo, em ordem de id, antes de ler a situação. Dois pedidos
        // simultâneos ficam em fila: o segundo vê o treinamento já revogado e responde 409. A criação da reserva
        // trava as mesmas linhas (FOR SHARE), e assim reservar e revogar também ficam em fila (ADR 0006).
        await using var transacao = await banco.Database.BeginTransactionAsync();
        await banco.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM treinamentos WHERE usuario_id = {alvo.UsuarioId} AND tipo_id = {alvo.TipoId} ORDER BY id FOR UPDATE""")
            .ToListAsync();

        // Lido depois da trava, para ver o estado já confirmado por quem veio antes.
        var daPessoaNoTipo = await banco.Treinamentos
            .Where(t => t.UsuarioId == alvo.UsuarioId && t.TipoId == alvo.TipoId)
            .ToListAsync();
        var treinamento = daPessoaNoTipo.Single(t => t.Id == id);

        var quemRevoga = Guid.Parse(User.FindFirstValue("sub")!);
        var agora = relogio.GetUtcNow();
        var motivo = pedido.Motivo.Trim();
        try
        {
            treinamento.Revogar(quemRevoga, motivo, agora);
        }
        catch (InvalidOperationException erro)
        {
            // Já revogado ou já vencido (as mensagens são do domínio).
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: TitulosDeErro.Para(StatusCodes.Status409Conflict),
                detail: erro.Message);
        }

        // Quem renovou antes de vencer ainda teria o registro anterior válido: a revogação vale para a pessoa
        // no tipo (decisão 6). O que já venceu ou já foi revogado fica como está.
        foreach (var outro in daPessoaNoTipo.Where(t => t.Id != id && t.SituacaoEm(agora) == SituacaoDoTreinamento.Valido))
        {
            outro.Revogar(quemRevoga, motivo, agora);
        }

        await banco.SaveChangesAsync();

        // Sem treinamento, a pessoa não opera mais as máquinas do tipo: cancela, na mesma transação, as reservas
        // ativas dela nessas máquinas que ainda não começaram (spec §4.6; plano do 1.5, decisões 6 e 8). A reserva em
        // andamento continua: a impressão já está rodando, e parar a máquina na hora é uma manutenção (1.7). A trava
        // do FOR UPDATE acima garante que uma reserva em criação (FOR SHARE nas mesmas linhas) já foi confirmada e é
        // vista aqui, ou ainda não leu os treinamentos e vai recusar a reserva (422). O UPDATE só pega reservas
        // ainda ativas: o cancelamento que chegou antes (do dono) fica como está.
        // Antes do UPDATE, trava as linhas candidatas em ordem de id (a mesma ordem da desmarcação): sem ordem comum,
        // dois UPDATEs de várias linhas sobre as reservas de um mesmo dono podem se esperar em ciclo (ADR 0007).
        await banco.Database
            .SqlQuery<Guid>($"""SELECT r.id AS "Value" FROM reservas r WHERE r.operador_id = {alvo.UsuarioId} AND r.situacao = 'Ativa' AND r.inicio > {agora} AND r.equipamento_id IN (SELECT e.id FROM equipamentos e WHERE e.tipo_id = {alvo.TipoId}) ORDER BY r.id FOR UPDATE""")
            .ToListAsync();
        await banco.Reservas
            .Where(r => r.OperadorId == alvo.UsuarioId
                && r.Situacao == SituacaoDaReserva.Ativa
                && r.Inicio > agora
                && banco.Equipamentos.Any(e => e.Id == r.EquipamentoId && e.TipoId == alvo.TipoId))
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Situacao, SituacaoDaReserva.CanceladaPelaEquipe)
                .SetProperty(r => r.CanceladaEm, agora)
                .SetProperty(r => r.CanceladaPorId, quemRevoga)
                .SetProperty(r => r.MotivoDoCancelamento, MotivoDoCancelamentoPorRevogacao));

        await transacao.CommitAsync();

        var linha = await Consultar().FirstAsync(l => l.Treinamento.Id == id);
        return Ok(Resposta(linha));
    }

    private IQueryable<Linha> Consultar() =>
        from t in banco.Treinamentos.AsNoTracking()
        join pessoa in banco.Users on t.UsuarioId equals pessoa.Id
        join tipo in banco.TiposDeEquipamento on t.TipoId equals tipo.Id
        join registrou in banco.Users on t.RegistradoPorId equals registrou.Id
        join revogou in banco.Users on t.RevogadoPorId equals (Guid?)revogou.Id into revogacoes
        from revogou in revogacoes.DefaultIfEmpty()
        select new Linha
        {
            Treinamento = t,
            UsuarioNome = pessoa.Nome,
            TipoNome = tipo.Nome,
            RegistradoPorNome = registrou.Nome,
            RevogadoPorNome = revogou.Nome,
        };

    private TreinamentoResposta Resposta(Linha linha)
    {
        var t = linha.Treinamento;
        return new TreinamentoResposta(
            t.Id,
            t.UsuarioId,
            linha.UsuarioNome,
            t.TipoId,
            linha.TipoNome,
            t.RealizadoEm,
            t.ValidoAte,
            t.SituacaoEm(relogio.GetUtcNow()),
            linha.RegistradoPorNome,
            t.RevogadoEm,
            linha.RevogadoPorNome,
            t.MotivoDaRevogacao);
    }

    private ObjectResult NaoEncontrado() =>
        Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: TitulosDeErro.Para(StatusCodes.Status404NotFound),
            detail: "Treinamento não encontrado.");
}
