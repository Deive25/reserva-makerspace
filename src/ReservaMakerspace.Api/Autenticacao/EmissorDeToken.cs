using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ReservaMakerspace.Api.Dados;

namespace ReservaMakerspace.Api.Autenticacao;

/// <summary>
/// Emite o JWT HS256. O token só prova quem é a pessoa (<c>sub</c>, <c>email</c>, <c>name</c>);
/// o papel NÃO vai nele e é lido do banco a cada pedido (ADR 0003).
/// </summary>
public sealed class EmissorDeToken(OpcoesJwt opcoes, TimeProvider relogio)
{
    private readonly JsonWebTokenHandler _manipulador = new();
    private readonly SigningCredentials _credenciais =
        new(new SymmetricSecurityKey(opcoes.ChaveEmBytes), SecurityAlgorithms.HmacSha256);

    public (string Token, DateTimeOffset ExpiraEm) Emitir(Usuario usuario)
    {
        var agora = relogio.GetUtcNow();
        var expiraEm = agora + OpcoesJwt.Validade;

        var descricao = new SecurityTokenDescriptor
        {
            Issuer = opcoes.Emissor,
            Audience = opcoes.Publico,
            IssuedAt = agora.UtcDateTime,
            NotBefore = agora.UtcDateTime,
            Expires = expiraEm.UtcDateTime,
            SigningCredentials = _credenciais,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, usuario.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, usuario.Email!),
                new Claim("name", usuario.Nome),
            ]),
        };

        return (_manipulador.CreateToken(descricao), expiraEm);
    }
}
