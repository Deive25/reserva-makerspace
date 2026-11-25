using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ReservaMakerspace.Api.Dados;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Api.Autenticacao;

/// <summary>
/// Cria a primeira Coordenação na subida, a partir de <c>CoordenacaoInicial__*</c>, se ainda não existir nenhuma.
/// </summary>
public static class CoordenacaoInicial
{
    public static async Task AplicarAsync(
        MakerspaceDbContext banco,
        UserManager<Usuario> usuarios,
        IConfiguration configuracao,
        ILogger logger)
    {
        if (await banco.Users.AnyAsync(u => u.Papel == Papel.Coordenacao))
        {
            return;
        }

        var email = configuracao["CoordenacaoInicial:Email"]?.Trim();
        var senha = configuracao["CoordenacaoInicial:Senha"];
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(senha))
        {
            logger.LogWarning(
                "Não há Coordenação e CoordenacaoInicial__Email ou CoordenacaoInicial__Senha não foram definidos: " +
                "ninguém poderá definir papéis.");
            return;
        }

        // Quem se cadastrou antes escolheu a própria senha: promover essa conta
        // daria a Coordenação a quem apenas chegou primeiro. Por isso a subida falha.
        if (await usuarios.FindByEmailAsync(email) is not null)
        {
            throw new InvalidOperationException("O e-mail de CoordenacaoInicial já tem conta. Use outro e-mail.");
        }

        var nome = configuracao["CoordenacaoInicial:Nome"];
        var coordenacao = new Usuario
        {
            UserName = email,
            Email = email,
            Nome = string.IsNullOrWhiteSpace(nome) ? "Coordenação" : nome.Trim(),
            Papel = Papel.Coordenacao,
        };
        var resultado = await usuarios.CreateAsync(coordenacao, senha);
        if (!resultado.Succeeded)
        {
            var motivos = string.Join(" ", resultado.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Não foi possível criar a Coordenação inicial: {motivos}");
        }

        logger.LogInformation("Coordenação inicial criada.");
    }
}
