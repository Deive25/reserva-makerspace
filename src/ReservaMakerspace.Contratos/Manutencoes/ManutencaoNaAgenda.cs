namespace ReservaMakerspace.Contratos.Manutencoes;

/// <summary>
/// Uma manutenção ativa na agenda de uma máquina e na lista <c>manutencoes</c> dos 409 (plano do 1.7, decisões 7 e 10).
/// Igual para todo mundo: o motivo fala da máquina, e quem registrou fica só nas rotas da Equipe.
/// </summary>
public record ManutencaoNaAgenda(Guid ManutencaoId, DateTimeOffset Inicio, DateTimeOffset Fim, string Motivo);
