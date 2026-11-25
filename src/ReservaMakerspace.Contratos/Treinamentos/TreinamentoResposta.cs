using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Contratos.Treinamentos;

/// <summary>
/// Um treinamento com os nomes já resolvidos. A <see cref="Situacao"/> vem do relógio da API na hora da consulta.
/// Os campos da revogação só vêm preenchidos quando a situação é <c>Revogado</c>.
/// </summary>
public record TreinamentoResposta(
    Guid Id,
    Guid UsuarioId,
    string UsuarioNome,
    Guid TipoId,
    string TipoNome,
    DateTimeOffset RealizadoEm,
    DateTimeOffset ValidoAte,
    SituacaoDoTreinamento Situacao,
    string RegistradoPorNome,
    DateTimeOffset? RevogadoEm,
    string? RevogadoPorNome,
    string? MotivoDaRevogacao);
