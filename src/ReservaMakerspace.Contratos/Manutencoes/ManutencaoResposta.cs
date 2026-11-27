using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Contratos.Manutencoes;

/// <summary>
/// Uma manutenção com os nomes já resolvidos, para a Equipe (quem registrou e quem encerrou aparecem só aqui). Os
/// horários saem em UTC. Uma manutenção encerrada no meio mostra o fim novo; <c>EncerradaEm</c> e
/// <c>EncerradaPorNome</c> só vêm preenchidos depois do encerramento.
/// </summary>
public record ManutencaoResposta(
    Guid Id,
    Guid EquipamentoId,
    string EquipamentoNome,
    DateTimeOffset Inicio,
    DateTimeOffset Fim,
    string Motivo,
    SituacaoDaManutencao Situacao,
    DateTimeOffset RegistradaEm,
    string RegistradaPorNome,
    DateTimeOffset? EncerradaEm,
    string? EncerradaPorNome);
