namespace ReservaMakerspace.Contratos.Contas;

public record RespostaDeLogin(string Token, DateTimeOffset ExpiraEm, UsuarioResposta Usuario);
