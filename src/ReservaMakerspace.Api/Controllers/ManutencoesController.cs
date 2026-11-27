using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReservaMakerspace.Api.Autenticacao;
using ReservaMakerspace.Api.Dados;
using ReservaMakerspace.Api.Erros;
using ReservaMakerspace.Contratos.Manutencoes;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Api.Controllers;

/// <summary>
/// Manutenções (spec §4.4, §4.5 e §5). Todas as rotas são da Equipe (e da Coordenação). Registrar e encerrar travam a
/// linha da máquina, a mesma que a criação da reserva, a desmarcação e o <c>PUT /equipamentos/{id}</c> travam (ADR 0006):
/// toda escrita que ocupa a agenda de uma máquina passa por ela (ADR 0008). Não há PUT nem DELETE: prorrogar é registrar
/// outra manutenção, encostada no fim da primeira. O "agora" vem só do <see cref="TimeProvider"/> da API.
/// </summary>
[ApiController]
[Authorize(Policy = Politicas.Equipe)]
public class ManutencoesController(MakerspaceDbContext banco, TimeProvider relogio) : ControllerBase
{
    /// <summary>A manutenção e os nomes que a resposta traz.</summary>
    // Classe com inicializador de objeto (e não record com construtor): o EF Core só consegue acrescentar
    // Where e OrderBy depois do select quando a projeção é desse tipo.
    private sealed class Linha
    {
        public required Manutencao Manutencao { get; init; }

        public required string EquipamentoNome { get; init; }

        public required string RegistradaPorNome { get; init; }

        public string? EncerradaPorNome { get; init; }
    }

    /// <summary>
    /// Todas as manutenções da máquina, inclusive as canceladas e as passadas, da que começa mais tarde para a mais
    /// antiga. Sem paginação, como <c>GET /reservas</c>.
    /// </summary>
    [HttpGet("/equipamentos/{equipamentoId:guid}/manutencoes")]
    [ProducesResponseType<List<ManutencaoResposta>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar(Guid equipamentoId)
    {
        if (!await banco.Equipamentos.AnyAsync(e => e.Id == equipamentoId))
        {
            return MaquinaNaoEncontrada();
        }

        var linhas = await Consultar()
            .Where(l => l.Manutencao.EquipamentoId == equipamentoId)
            .OrderByDescending(l => l.Manutencao.Inicio).ThenBy(l => l.Manutencao.Id)
            .ToListAsync();
        return Ok(linhas.Select(Resposta).ToList());
    }

    [HttpGet("/manutencoes/{id:guid}")]
    [ProducesResponseType<ManutencaoResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Buscar(Guid id)
    {
        var linha = await Consultar().FirstOrDefaultAsync(l => l.Manutencao.Id == id);
        return linha is null ? NaoEncontrada() : Ok(Resposta(linha));
    }

    /// <summary>
    /// Registra uma manutenção e cancela, na mesma transação, as reservas ativas que cruzam o intervalo (plano do 1.7,
    /// decisão 5). A ordem dos códigos (decisão 3): formato (400), máquina (404), a regra do início (422) e, por último, a
    /// agenda (409). A transação trava, nesta ordem e até o commit: a linha da máquina e, só depois de ler o que decide,
    /// as linhas das reservas a cancelar, em ordem de id (a ordem comum dos ADRs 0006 e 0007). Uma máquina inativa também
    /// aceita: registrar o conserto de uma máquina parada não tira a vaga de ninguém.
    /// </summary>
    [HttpPost("/equipamentos/{equipamentoId:guid}/manutencoes")]
    [ProducesResponseType<ManutencaoResposta>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Registrar(Guid equipamentoId, PedidoDeManutencao pedido)
    {
        var fim = pedido.Fim!.Value;
        // Sem início, a manutenção começa agora (decisão 4).
        if (fim <= (pedido.Inicio ?? relogio.GetUtcNow()))
        {
            ModelState.AddModelError("fim", "O fim precisa ser depois do início.");
            return ValidationProblem(ModelState);
        }

        var eu = QuemSou();
        await using var transacao = await banco.Database.BeginTransactionAsync();

        // Trava a linha da máquina (FOR NO KEY UPDATE, a mesma da criação da reserva) antes de ler o que decide: quem
        // esperou na fila vê o que o outro confirmou.
        var achou = await banco.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM equipamentos WHERE id = {equipamentoId} FOR NO KEY UPDATE""")
            .ToListAsync();
        if (achou.Count == 0)
        {
            return MaquinaNaoEncontrada();
        }

        var agora = relogio.GetUtcNow();
        Manutencao manutencao;
        try
        {
            manutencao = Manutencao.Registrar(equipamentoId, eu, pedido.Inicio, fim, pedido.Motivo, agora);
        }
        catch (ManutencaoRecusadaException erro)
        {
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: TitulosDeErro.Para(StatusCodes.Status422UnprocessableEntity),
                detail: erro.Message);
        }
        catch (ArgumentException erro)
        {
            ModelState.AddModelError(erro.ParamName ?? "fim", erro.Message);
            return ValidationProblem(ModelState);
        }

        // Só uma manutenção ativa por vez no intervalo: quem chegou antes já confirmou e aparece aqui.
        var outras = await banco.ManutencoesAtivasQueCruzamAsync(equipamentoId, manutencao.Inicio, manutencao.Fim);
        if (outras.Count > 0)
        {
            return ConflitoComManutencoes(outras);
        }

        // As reservas ativas que cruzam o intervalo, inclusive a em andamento e as das duas finalidades. Antes do UPDATE,
        // trava as linhas em ordem de id, a mesma ordem da revogação e da desmarcação: dois UPDATEs de várias linhas sem
        // ordem comum podem se esperar em ciclo (ADR 0007). O UPDATE só pega as ainda ativas: se o dono cancelou no meio,
        // o motivo de quem cancelou primeiro fica.
        var inicio = manutencao.Inicio;
        await banco.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM reservas WHERE equipamento_id = {equipamentoId} AND situacao = 'Ativa' AND inicio < {manutencao.Fim} AND {inicio} < fim ORDER BY id FOR UPDATE""")
            .ToListAsync();
        await banco.Reservas
            .Where(r => r.EquipamentoId == equipamentoId
                && r.Situacao == SituacaoDaReserva.Ativa
                && r.Inicio < manutencao.Fim
                && inicio < r.Fim)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Situacao, SituacaoDaReserva.CanceladaPorManutencao)
                .SetProperty(r => r.CanceladaEm, agora)
                .SetProperty(r => r.CanceladaPorId, eu)
                .SetProperty(r => r.MotivoDoCancelamento, manutencao.Motivo));

        banco.Manutencoes.Add(manutencao);
        try
        {
            await banco.SaveChangesAsync();
        }
        catch (DbUpdateException erro) when (
            ViolacaoDeExclusao.Eh(erro, MakerspaceDbContext.RestricaoDeSobreposicaoDasManutencoes))
        {
            // A garantia final do banco: só chega aqui um caminho que escapou da trava. A transação já falhou e não
            // aceita outra consulta: sem a lista, e o cancelamento das reservas é desfeito junto.
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: TitulosDeErro.Para(StatusCodes.Status409Conflict),
                detail: MensagemDeOutraManutencao);
        }

        await transacao.CommitAsync();

        var linha = await Consultar().FirstAsync(l => l.Manutencao.Id == manutencao.Id);
        return Created($"/manutencoes/{manutencao.Id}", Resposta(linha));
    }

    /// <summary>
    /// Encerra a manutenção antes do fim (decisão 2): encurta a que está em andamento e cancela a que ainda não começou.
    /// Também trava a máquina (decisão 11): lê a manutenção, trava a máquina dela, relê e decide. Assim dois encerramentos
    /// da mesma manutenção ficam em fila, e o segundo vê o estado novo e recebe 409. As reservas canceladas não voltam.
    /// </summary>
    [HttpPost("/manutencoes/{id:guid}/encerramento")]
    [ProducesResponseType<ManutencaoResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Encerrar(Guid id)
    {
        // A máquina da manutenção nunca muda; por isso pode ser lida antes da trava.
        var equipamentoId = await banco.Manutencoes.AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => (Guid?)m.EquipamentoId)
            .FirstOrDefaultAsync();
        if (equipamentoId is null)
        {
            return NaoEncontrada();
        }

        await using var transacao = await banco.Database.BeginTransactionAsync();
        await banco.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM equipamentos WHERE id = {equipamentoId} FOR NO KEY UPDATE""")
            .ToListAsync();

        // Lida depois da trava, para ver o estado já confirmado por quem veio antes.
        var manutencao = await banco.Manutencoes.FirstAsync(m => m.Id == id);
        try
        {
            manutencao.Encerrar(QuemSou(), relogio.GetUtcNow());
        }
        catch (InvalidOperationException erro)
        {
            // Já acabou ou já foi cancelada (as mensagens são do domínio).
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: TitulosDeErro.Para(StatusCodes.Status409Conflict),
                detail: erro.Message);
        }

        await banco.SaveChangesAsync();
        await transacao.CommitAsync();

        var linha = await Consultar().FirstAsync(l => l.Manutencao.Id == id);
        return Ok(Resposta(linha));
    }

    private const string MensagemDeOutraManutencao = "A máquina já tem uma manutenção ativa nesse horário.";

    private IQueryable<Linha> Consultar() =>
        from m in banco.Manutencoes.AsNoTracking()
        join maquina in banco.Equipamentos on m.EquipamentoId equals maquina.Id
        join registrou in banco.Users on m.RegistradaPorId equals registrou.Id
        join encerrou in banco.Users on m.EncerradaPorId equals (Guid?)encerrou.Id into encerramentos
        from encerrou in encerramentos.DefaultIfEmpty()
        select new Linha
        {
            Manutencao = m,
            EquipamentoNome = maquina.Nome,
            RegistradaPorNome = registrou.Nome,
            EncerradaPorNome = encerrou.Nome,
        };

    private static ManutencaoResposta Resposta(Linha linha)
    {
        var m = linha.Manutencao;
        return new ManutencaoResposta(
            m.Id,
            m.EquipamentoId,
            linha.EquipamentoNome,
            m.Inicio,
            m.Fim,
            m.Motivo,
            m.Situacao,
            m.RegistradaEm,
            linha.RegistradaPorNome,
            m.EncerradaEm,
            linha.EncerradaPorNome);
    }

    private Guid QuemSou() => Guid.Parse(User.FindFirstValue("sub")!);

    private ObjectResult ConflitoComManutencoes(List<ManutencaoNaAgenda> manutencoes)
    {
        var problema = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext,
            StatusCodes.Status409Conflict,
            title: TitulosDeErro.Para(StatusCodes.Status409Conflict),
            detail: MensagemDeOutraManutencao);
        problema.Extensions["manutencoes"] = manutencoes;
        return new ObjectResult(problema) { StatusCode = StatusCodes.Status409Conflict };
    }

    private ObjectResult MaquinaNaoEncontrada() =>
        Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: TitulosDeErro.Para(StatusCodes.Status404NotFound),
            detail: "Máquina não encontrada.");

    private ObjectResult NaoEncontrada() =>
        Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: TitulosDeErro.Para(StatusCodes.Status404NotFound),
            detail: "Manutenção não encontrada.");
}
