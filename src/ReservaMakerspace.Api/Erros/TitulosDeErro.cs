namespace ReservaMakerspace.Api.Erros;

/// <summary>Títulos em português dos erros em ProblemDetails, por código HTTP.</summary>
public static class TitulosDeErro
{
    private static readonly Dictionary<int, string> Titulos = new()
    {
        [StatusCodes.Status400BadRequest] = "Requisição inválida",
        [StatusCodes.Status401Unauthorized] = "Não autenticado",
        [StatusCodes.Status403Forbidden] = "Acesso negado",
        [StatusCodes.Status404NotFound] = "Recurso não encontrado",
        [StatusCodes.Status409Conflict] = "Conflito",
        [StatusCodes.Status422UnprocessableEntity] = "Regra de negócio violada",
        [StatusCodes.Status500InternalServerError] = "Erro interno",
    };

    /// <summary>Devolve o título do código, ou <c>null</c> se ele não tiver um título próprio.</summary>
    public static string? Para(int status) => Titulos.GetValueOrDefault(status);
}
