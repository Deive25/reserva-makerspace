using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ReservaMakerspace.Contratos.Equipamentos;
using ReservaMakerspace.Contratos.Reservas;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Api.Dados;

/// <summary>Uma reserva ativa lida para a agenda ou para uma lista de conflitos, com o nome do operador.</summary>
// Classe com inicializador de objeto (e não record com construtor): o EF Core só consegue acrescentar
// Where e OrderBy depois do select quando a projeção é desse tipo.
public sealed class LinhaDaAgenda
{
    public required Guid Id { get; init; }

    public required Guid OperadorId { get; init; }

    public required string OperadorNome { get; init; }

    public required DateTimeOffset Inicio { get; init; }

    public required DateTimeOffset Fim { get; init; }

    public required FinalidadeDaReserva Finalidade { get; init; }

    public string? Solicitante { get; init; }

    /// <summary>
    /// A forma pública (plano do 1.6, decisão 3). A Equipe e a Coordenação veem o id, o nome e o solicitante de todas;
    /// o Usuário, só os das reservas dele. O nome do operador de outra pessoa nunca sai para o Usuário.
    /// </summary>
    public ReservaNaAgenda Visivel(Guid quemPede, bool ehDaEquipe) =>
        ehDaEquipe || OperadorId == quemPede
            ? new ReservaNaAgenda(Inicio, Fim, Finalidade, Id, OperadorNome, Solicitante)
            : new ReservaNaAgenda(Inicio, Fim, Finalidade);
}

/// <summary>As duas consultas da agenda que a disponibilidade, a agenda e a criação da reserva dividem.</summary>
public static class ConsultasDaAgenda
{
    /// <summary>As reservas ativas que cruzam <c>[inicio, fim)</c> na máquina, em ordem de início.</summary>
    public static Task<List<LinhaDaAgenda>> ReservasAtivasQueCruzamAsync(
        this MakerspaceDbContext banco, Guid equipamentoId, DateTimeOffset inicio, DateTimeOffset fim) =>
        banco.LinhasAsync(r =>
            r.EquipamentoId == equipamentoId
            && r.Situacao == SituacaoDaReserva.Ativa
            && r.Inicio < fim
            && inicio < r.Fim);

    /// <summary>Lê as reservas que cumprem o filtro, com o nome do operador, em ordem de início.</summary>
    public static Task<List<LinhaDaAgenda>> LinhasAsync(
        this MakerspaceDbContext banco, Expression<Func<Reserva, bool>> filtro) =>
        (from r in banco.Reservas.AsNoTracking().Where(filtro)
         join operador in banco.Users on r.OperadorId equals operador.Id
         orderby r.Inicio, r.Id
         select new LinhaDaAgenda
         {
             Id = r.Id,
             OperadorId = r.OperadorId,
             OperadorNome = operador.Nome,
             Inicio = r.Inicio,
             Fim = r.Fim,
             Finalidade = r.Finalidade,
             Solicitante = r.Solicitante,
         }).ToListAsync();

    /// <summary>
    /// A consulta única de "máquinas livres" (plano do 1.6, decisão 4): as máquinas ativas do tipo sem reserva ativa que
    /// cruze <c>[inicio, fim)</c>, em ordem de nome. A disponibilidade e a criação da reserva usam esta; no 1.7 a
    /// manutenção entra aqui. Não trava nada: quem precisa de uma leitura estável decide isso por fora.
    /// </summary>
    public static Task<List<EquipamentoResposta>> MaquinasLivresAsync(
        this MakerspaceDbContext banco, Guid tipoId, DateTimeOffset inicio, DateTimeOffset fim) =>
        banco.Equipamentos.AsNoTracking()
            .Where(e => e.TipoId == tipoId
                && e.Ativo
                && !banco.Reservas.Any(r =>
                    r.EquipamentoId == e.Id
                    && r.Situacao == SituacaoDaReserva.Ativa
                    && r.Inicio < fim
                    && inicio < r.Fim))
            .OrderBy(e => e.Nome).ThenBy(e => e.Id)
            .Select(e => new EquipamentoResposta(e.Id, e.Nome, e.TipoId, e.Tipo.Nome, e.Ativo))
            .ToListAsync();
}
