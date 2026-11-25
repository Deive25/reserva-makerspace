using System.Security.Cryptography;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>Monta a configuração das fábricas de teste, com segredos gerados na hora.</summary>
public static class ConfiguracaoDeTeste
{
    /// <summary>Chave aleatória de 48 bytes (64 caracteres em base64), maior que o mínimo de 32.</summary>
    public static string NovaChave() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public static Dictionary<string, string?> Para(
        string connectionString,
        string chave,
        string? coordenacaoEmail = null,
        string? coordenacaoSenha = null,
        bool aplicarMigracoes = true,
        bool aplicarDadosDeExemplo = false,
        string? senhaDeExemplo = null) => new()
    {
        ["ConnectionStrings:Makerspace"] = connectionString,
        ["Jwt:Chave"] = chave,
        ["CoordenacaoInicial:Email"] = coordenacaoEmail,
        ["CoordenacaoInicial:Senha"] = coordenacaoSenha,
        ["Banco:AplicarMigracoesNaSubida"] = aplicarMigracoes ? "true" : "false",
        // O WebApplicationFactory roda em Development e leria o appsettings.Development.json: desligada por padrão.
        ["DadosDeExemplo:Aplicar"] = aplicarDadosDeExemplo ? "true" : "false",
        ["DadosDeExemplo:Senha"] = senhaDeExemplo,
    };
}
