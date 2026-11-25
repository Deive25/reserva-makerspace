using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ReservaMakerspace.Api.Dados;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Api.Autenticacao;

public static class AutenticacaoExtensions
{
    /// <summary>Identity (usuários e hash de senha, sem cookies), emissão e validação do JWT e as políticas.</summary>
    public static IServiceCollection AddAutenticacao(this IServiceCollection servicos, OpcoesJwt jwt)
    {
        servicos.AddIdentityCore<Usuario>(opcoes =>
            {
                opcoes.User.RequireUniqueEmail = true;
                // Aceita e-mails com apóstrofo ou acento como nome de usuário.
                opcoes.User.AllowedUserNameCharacters = "";

                // NIST SP 800-63B: tamanho mínimo, sem regras de composição.
                opcoes.Password.RequiredLength = 15;
                opcoes.Password.RequireDigit = false;
                opcoes.Password.RequireLowercase = false;
                opcoes.Password.RequireUppercase = false;
                opcoes.Password.RequireNonAlphanumeric = false;
                opcoes.Password.RequiredUniqueChars = 1;
            })
            .AddEntityFrameworkStores<MakerspaceDbContext>();

        servicos.AddSingleton(TimeProvider.System);
        servicos.AddSingleton(jwt);
        servicos.AddSingleton<EmissorDeToken>();

        servicos.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opcoes =>
            {
                opcoes.MapInboundClaims = false;
                opcoes.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(jwt.ChaveEmBytes),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RequireSignedTokens = true,
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Emissor,
                    ValidateAudience = true,
                    ValidAudience = jwt.Publico,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name",
                    RoleClaimType = "role",
                };
                opcoes.Events = new JwtBearerEvents { OnTokenValidated = AplicarPapelAtualAsync };
            });

        servicos.AddAuthorization(opcoes =>
        {
            // Seguro por padrão: toda rota exige login, e só as públicas são marcadas com AllowAnonymous.
            opcoes.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            opcoes.AddPolicy(Politicas.Equipe, p => p.RequireRole(nameof(Papel.Equipe), nameof(Papel.Coordenacao)));
            opcoes.AddPolicy(Politicas.Coordenacao, p => p.RequireRole(nameof(Papel.Coordenacao)));
        });

        return servicos;
    }

    /// <summary>
    /// O token prova quem é a pessoa; o papel vem do banco a cada pedido. Assim promover ou rebaixar vale
    /// no pedido seguinte, e um token de conta que não existe mais deixa de valer.
    /// </summary>
    private static async Task AplicarPapelAtualAsync(TokenValidatedContext contexto)
    {
        var identidade = contexto.Principal?.Identity as ClaimsIdentity;
        var sub = identidade?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (identidade is null || !Guid.TryParse(sub, out var id))
        {
            contexto.Fail("Token sem identificação válida.");
            return;
        }

        var banco = contexto.HttpContext.RequestServices.GetRequiredService<MakerspaceDbContext>();
        var papel = await banco.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => (Papel?)u.Papel)
            .FirstOrDefaultAsync(contexto.HttpContext.RequestAborted);
        if (papel is null)
        {
            contexto.Fail("A conta do token não existe mais.");
            return;
        }

        identidade.AddClaim(new Claim("role", papel.Value.ToString()));
    }
}
