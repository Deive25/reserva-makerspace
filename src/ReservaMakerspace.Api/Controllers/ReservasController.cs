using System.Security.Claims;
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
/// Reservas (spec §4.2 e §5). Criar e cancelar são de qualquer pessoa logada (a reserva é sempre do próprio
/// operador); <c>GET /reservas</c> é da Equipe. <c>GET /me/reservas</c> fica aqui para a consulta e a resposta
/// ficarem num lugar só. Não há PUT nem DELETE: mudar uma reserva é cancelar e reservar de novo.
/// Os horários entram com fuso e saem em UTC; o "agora" vem só do <see cref="TimeProvider"/> da API.
/// </summary>
[ApiController]
[Route("reservas")]
public class ReservasController(MakerspaceDbContext banco, TimeProvider relogio) : ControllerBase
{
    /// <summary>A reserva e os nomes que a resposta traz.</summary>
    // Classe com inicializador de objeto (e não record com construtor): o EF Core só consegue acrescentar
    // Where e OrderBy depois do select quando a projeção é desse tipo.
    private sealed class Linha
    {
        public required Reserva Reserva { get; init; }

        public required string EquipamentoNome { get; init; }

        public required string TipoNome { get; init; }

        public required string OperadorNome { get; init; }

        public string? CanceladaPorNome { get; init; }
    }

    [HttpGet]
    [Authorize(Policy = Politicas.Equipe)]
    [ProducesResponseType<List<ReservaResposta>>(StatusCodes.Status200OK)]
    public async Task<List<ReservaResposta>> Listar([FromQuery] Guid? usuarioId, [FromQuery] Guid? equipamentoId)
    {
        var consulta = Consultar();
        if (usuarioId is not null)
        {
            consulta = consulta.Where(l => l.Reserva.OperadorId == usuarioId);
        }

        if (equipamentoId is not null)
        {
            consulta = consulta.Where(l => l.Reserva.EquipamentoId == equipamentoId);
        }

        var linhas = await consulta
            .OrderByDescending(l => l.Reserva.Inicio).ThenBy(l => l.Reserva.Id)
            .ToListAsync();
        return linhas.Select(Resposta).ToList();
    }

    [HttpGet("/me/reservas")]
    [ProducesResponseType<List<ReservaResposta>>(StatusCodes.Status200OK)]
    public async Task<List<ReservaResposta>> ListarMinhas()
    {
        var eu = QuemSou();
        var linhas = await Consultar()
            .Where(l => l.Reserva.OperadorId == eu)
            .OrderByDescending(l => l.Reserva.Inicio).ThenBy(l => l.Reserva.Id)
            .ToListAsync();
        return linhas.Select(Resposta).ToList();
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ReservaResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Buscar(Guid id)
    {
        var linha = await Consultar().FirstOrDefaultAsync(l => l.Reserva.Id == id);
        if (linha is null)
        {
            return NaoEncontrada();
        }

        // O dono ou a Equipe (spec §5: 403 para "reserva de outra pessoa").
        return linha.Reserva.OperadorId == QuemSou() || EhDaEquipe() ? Ok(Resposta(linha)) : DeOutraPessoa();
    }

    /// <summary>
    /// Cria uma reserva. A ordem dos códigos (plano do 1.5, decisão 3): formato (400), papel (403), regras (422) e,
    /// por último, a agenda (409). A transação trava, nesta ordem e até o commit: a máquina, os treinamentos da pessoa
    /// no tipo e (só no projeto pessoal) a linha da pessoa. Só depois lê o que decide: em READ COMMITTED cada comando
    /// enxerga o que já foi confirmado quando começa, e por isso a leitura vem depois da trava (ADR 0006).
    /// Numa demanda do parque que cruza reservas pessoais, as travas da desmarcação (<see cref="Desmarcacao"/>) decidem se
    /// o pedido as cancela, na mesma transação e antes do <c>INSERT</c> da demanda (ADR 0007). Todo 409 de agenda traz também
    /// as máquinas livres do tipo.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ReservaResposta>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Criar(PedidoDeReserva pedido)
    {
        var equipamentoId = pedido.EquipamentoId!.Value;
        var inicio = pedido.Inicio!.Value;
        var fim = pedido.Fim!.Value;
        var finalidade = pedido.Finalidade!.Value;

        if (fim <= inicio)
        {
            ModelState.AddModelError("fim", "O fim precisa ser depois do início.");
        }

        if (finalidade == FinalidadeDaReserva.ProjetoPessoal && Reserva.Normalizar(pedido.Solicitante) is not null)
        {
            ModelState.AddModelError("solicitante", "O solicitante só existe na demanda do parque.");
        }

        if (finalidade == FinalidadeDaReserva.ProjetoPessoal && Reserva.Normalizar(pedido.MotivoDesmarcacao) is not null)
        {
            ModelState.AddModelError("motivoDesmarcacao", "O motivo da desmarcação só existe na demanda do parque.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var eu = QuemSou();
        var papel = PapelDeQuemPede();

        await using var transacao = await banco.Database.BeginTransactionAsync();

        // Trava (a): a linha da máquina. FOR NO KEY UPDATE põe em fila quem trava a mesma linha, sem segurar quem
        // só grava uma linha apontando para ela (a checagem da chave estrangeira). Só depois lê o ativo e o tipo.
        var achou = await banco.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM equipamentos WHERE id = {equipamentoId} FOR NO KEY UPDATE""")
            .ToListAsync();
        if (achou.Count == 0)
        {
            // Um id inexistente no corpo é erro de validação (400); o 404 fica para o id da URL.
            ModelState.AddModelError("equipamentoId", "Máquina não encontrada.");
            return ValidationProblem(ModelState);
        }

        var maquina = await banco.Equipamentos.AsNoTracking().Include(e => e.Tipo).FirstAsync(e => e.Id == equipamentoId);

        // Trava (b): os treinamentos da pessoa no tipo, em ordem de id, em modo compartilhado. Duas reservas da mesma
        // pessoa não se bloqueiam, mas a revogação (FOR UPDATE nas mesmas linhas) espera ou é esperada.
        await banco.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM treinamentos WHERE usuario_id = {eu} AND tipo_id = {maquina.TipoId} ORDER BY id FOR SHARE""")
            .ToListAsync();
        var treinadoAte = await banco.Treinamentos
            .Where(t => t.UsuarioId == eu && t.TipoId == maquina.TipoId && t.RevogadoEm == null)
            .MaxAsync(t => (DateTimeOffset?)t.ValidoAte);

        var agora = relogio.GetUtcNow();
        var pessoaisAtivas = 0;
        if (finalidade == FinalidadeDaReserva.ProjetoPessoal)
        {
            // Trava (c): a linha da pessoa, antes de contar. Sem ela, dois pedidos da mesma pessoa em máquinas
            // diferentes contariam 1 cada e passariam os dois. A variante NO KEY não segura quem só grava uma linha
            // apontando para a pessoa (registrar ou revogar um treinamento dela).
            await banco.Database
                .SqlQuery<Guid>($"""SELECT id AS "Value" FROM usuarios WHERE id = {eu} FOR NO KEY UPDATE""")
                .ToListAsync();
            pessoaisAtivas = await banco.Reservas.CountAsync(r =>
                r.OperadorId == eu
                && r.Finalidade == FinalidadeDaReserva.ProjetoPessoal
                && r.Situacao == SituacaoDaReserva.Ativa
                && r.Fim > agora);
        }

        Reserva reserva;
        try
        {
            reserva = Reserva.Criar(
                maquina, eu, papel, finalidade, inicio, fim, pedido.Solicitante, pedido.Descricao,
                treinadoAte, pessoaisAtivas, agora);
        }
        catch (ReservaRecusadaException erro)
        {
            var status = erro.Motivo == MotivoDaRecusa.SemPermissao
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status422UnprocessableEntity;
            return Problem(statusCode: status, title: TitulosDeErro.Para(status), detail: erro.Message);
        }

        // Regra 4, com a máquina travada: quem chegou antes já confirmou a reserva e aparece aqui.
        var conflitos = await banco.ReservasAtivasQueCruzamAsync(equipamentoId, reserva.Inicio, reserva.Fim);
        if (conflitos.Count > 0)
        {
            // Todo 409 de agenda traz as máquinas livres do tipo (decisão 4), com a mesma consulta da disponibilidade.
            var livres = await banco.MaquinasLivresAsync(maquina.TipoId, reserva.Inicio, reserva.Fim, exceto: equipamentoId);
            var visiveis = conflitos.Select(c => c.Visivel(eu, EhDaEquipe())).ToList();

            if (finalidade == FinalidadeDaReserva.ProjetoPessoal)
            {
                return ConflitoDaAgenda(visiveis, livres);
            }

            // Demanda do parque: as travas da desmarcação (§4.3), no domínio. Qualquer recusa é 409, porque o que
            // impede a reserva continua sendo a agenda; a mensagem diz por que desmarcar não resolve.
            var recusa = Desmarcacao.Avaliar(
                conflitos.Select(c => new ReservaEmConflito(c.Inicio, c.Finalidade)).ToList(),
                pedido.MotivoDesmarcacao,
                livres.FirstOrDefault()?.Nome,
                agora);
            if (recusa is not null)
            {
                return ConflitoDaAgenda(visiveis, livres, recusa.Mensagem);
            }

            // Todas as travas passaram: cancela as pessoais na mesma transação da criação, sob a trava da máquina.
            // O UPDATE só pega as ainda ativas: se o dono cancelou no meio, a vaga ficou livre de qualquer jeito e
            // o motivo de quem cancelou primeiro fica (ADR 0006).
            var ids = conflitos.Select(c => c.Id).ToList();
            var motivo = Reserva.Normalizar(pedido.MotivoDesmarcacao);
            await banco.Reservas
                .Where(r => ids.Contains(r.Id) && r.Situacao == SituacaoDaReserva.Ativa)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.Situacao, SituacaoDaReserva.CanceladaPelaEquipe)
                    .SetProperty(r => r.CanceladaEm, agora)
                    .SetProperty(r => r.CanceladaPorId, eu)
                    .SetProperty(r => r.MotivoDoCancelamento, motivo));
        }


        banco.Reservas.Add(reserva);
        try
        {
            await banco.SaveChangesAsync();
        }
        catch (DbUpdateException erro) when (
            ViolacaoDeExclusao.Eh(erro, MakerspaceDbContext.RestricaoDeSobreposicaoDasReservas))
        {
            // A garantia final do banco: só chega aqui um caminho que escapou da trava. A transação já falhou e não
            // aceita outra consulta: sem as duas listas (ADR 0006), e a desmarcação, se houve, é desfeita junto.
            return ConflitoDaAgenda(null, null);
        }

        await transacao.CommitAsync();

        var linha = await Consultar().FirstAsync(l => l.Reserva.Id == reserva.Id);
        return Created($"/reservas/{reserva.Id}", Resposta(linha));
    }

    /// <summary>
    /// Só o dono cancela, e só antes do início. O cancelamento é um <c>UPDATE</c> condicional: quem chega depois
    /// de outro cancelamento encontra 0 linhas e recebe 409, e o motivo de quem cancelou primeiro fica (decisão 10).
    /// </summary>
    [HttpPost("{id:guid}/cancelamento")]
    [ProducesResponseType<ReservaResposta>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancelar(Guid id)
    {
        // Lido só para escolher entre 404, 403 e 409; quem decide o cancelamento é o UPDATE condicional.
        var dono = await banco.Reservas.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => (Guid?)r.OperadorId)
            .FirstOrDefaultAsync();
        if (dono is null)
        {
            return NaoEncontrada();
        }

        var eu = QuemSou();
        if (dono != eu)
        {
            // A Equipe também: desmarcar a reserva de outra pessoa tem as travas do 1.6.
            return DeOutraPessoa();
        }

        var agora = relogio.GetUtcNow();
        var linhas = await banco.Reservas
            .Where(r => r.Id == id && r.Situacao == SituacaoDaReserva.Ativa && r.Inicio > agora)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Situacao, SituacaoDaReserva.CanceladaPeloDono)
                .SetProperty(r => r.CanceladaEm, agora)
                .SetProperty(r => r.CanceladaPorId, eu));
        if (linhas == 0)
        {
            var situacao = await banco.Reservas.AsNoTracking()
                .Where(r => r.Id == id)
                .Select(r => r.Situacao)
                .FirstAsync();
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: TitulosDeErro.Para(StatusCodes.Status409Conflict),
                detail: situacao == SituacaoDaReserva.Ativa ? "A reserva já começou." : "A reserva já foi cancelada.");
        }

        var linha = await Consultar().FirstAsync(l => l.Reserva.Id == id);
        return Ok(Resposta(linha));
    }

    private IQueryable<Linha> Consultar() =>
        from r in banco.Reservas.AsNoTracking()
        join maquina in banco.Equipamentos on r.EquipamentoId equals maquina.Id
        join tipo in banco.TiposDeEquipamento on maquina.TipoId equals tipo.Id
        join operador in banco.Users on r.OperadorId equals operador.Id
        join canceladora in banco.Users on r.CanceladaPorId equals (Guid?)canceladora.Id into cancelamentos
        from canceladora in cancelamentos.DefaultIfEmpty()
        select new Linha
        {
            Reserva = r,
            EquipamentoNome = maquina.Nome,
            TipoNome = tipo.Nome,
            OperadorNome = operador.Nome,
            CanceladaPorNome = canceladora.Nome,
        };

    private static ReservaResposta Resposta(Linha linha)
    {
        var r = linha.Reserva;
        return new ReservaResposta(
            r.Id,
            r.EquipamentoId,
            linha.EquipamentoNome,
            linha.TipoNome,
            r.OperadorId,
            linha.OperadorNome,
            r.Inicio,
            r.Fim,
            r.Finalidade,
            r.Solicitante,
            r.Descricao,
            r.Situacao,
            r.CriadaEm,
            r.CanceladaEm,
            linha.CanceladaPorNome,
            r.MotivoDoCancelamento);
    }

    private Guid QuemSou() => Guid.Parse(User.FindFirstValue("sub")!);

    private bool EhDaEquipe() => User.IsInRole(nameof(Papel.Equipe)) || User.IsInRole(nameof(Papel.Coordenacao));

    // O papel vem do banco a cada pedido (AplicarPapelAtualAsync) e vira a claim "role".
    private Papel PapelDeQuemPede() =>
        User.IsInRole(nameof(Papel.Coordenacao)) ? Papel.Coordenacao
        : User.IsInRole(nameof(Papel.Equipe)) ? Papel.Equipe
        : Papel.Usuario;

    /// <summary>
    /// 409 de agenda. <paramref name="conflitos"/> e <paramref name="maquinasLivres"/> são <c>null</c> quando quem
    /// recusou foi a restrição do banco. <paramref name="detalhe"/> traz a mensagem da trava que recusou a desmarcação.
    /// </summary>
    private ObjectResult ConflitoDaAgenda(
        List<ReservaNaAgenda>? conflitos,
        List<EquipamentoResposta>? maquinasLivres,
        string detalhe = Desmarcacao.MensagemDoConflito)
    {
        var problema = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext,
            StatusCodes.Status409Conflict,
            title: TitulosDeErro.Para(StatusCodes.Status409Conflict),
            detail: detalhe);
        if (conflitos is not null)
        {
            problema.Extensions["conflitos"] = conflitos;
        }

        if (maquinasLivres is not null)
        {
            problema.Extensions["maquinasLivres"] = maquinasLivres;
        }

        return new ObjectResult(problema) { StatusCode = StatusCodes.Status409Conflict };
    }


    private ObjectResult DeOutraPessoa() =>
        Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: TitulosDeErro.Para(StatusCodes.Status403Forbidden),
            detail: "Esta reserva é de outra pessoa.");

    private ObjectResult NaoEncontrada() =>
        Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: TitulosDeErro.Para(StatusCodes.Status404NotFound),
            detail: "Reserva não encontrada.");
}
