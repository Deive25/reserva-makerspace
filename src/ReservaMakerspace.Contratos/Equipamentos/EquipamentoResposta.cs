namespace ReservaMakerspace.Contratos.Equipamentos;

/// <summary>Traz o nome do tipo para a agenda não precisar buscar cada tipo.</summary>
public record EquipamentoResposta(Guid Id, string Nome, Guid TipoId, string TipoNome, bool Ativo);
