namespace ReservaMakerspace.Api.Autenticacao;

/// <summary>Nomes das políticas de autorização. Os papéis são cumulativos.</summary>
public static class Politicas
{
    /// <summary>Equipe ou Coordenação.</summary>
    public const string Equipe = "Equipe";

    /// <summary>Só a Coordenação.</summary>
    public const string Coordenacao = "Coordenacao";
}
