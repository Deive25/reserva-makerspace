using System.Text;

namespace ReservaMakerspace.Api.Autenticacao;

/// <summary>Configuração do JWT. A chave só vem de <c>Jwt__Chave</c> (ambiente), nunca de arquivo versionado.</summary>
public sealed record OpcoesJwt(string Chave, string Emissor, string Publico)
{
    private const int TamanhoMinimoDaChaveEmBytes = 32;

    /// <summary>Validade do token. Sem refresh: passou disso, a pessoa entra de novo.</summary>
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(60);

    public byte[] ChaveEmBytes => Encoding.UTF8.GetBytes(Chave);

    public static OpcoesJwt Ler(IConfiguration configuracao)
    {
        var chave = configuracao["Jwt:Chave"];
        if (string.IsNullOrWhiteSpace(chave) || Encoding.UTF8.GetByteCount(chave) < TamanhoMinimoDaChaveEmBytes)
        {
            throw new InvalidOperationException("Configure Jwt__Chave com pelo menos 32 caracteres.");
        }

        const string padrao = "reserva-makerspace";
        return new OpcoesJwt(
            chave,
            configuracao["Jwt:Emissor"] is { Length: > 0 } emissor ? emissor : padrao,
            configuracao["Jwt:Publico"] is { Length: > 0 } publico ? publico : padrao);
    }
}
