using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Contratos.Contas;

public record UsuarioResposta(Guid Id, string Nome, string Email, Papel Papel);
