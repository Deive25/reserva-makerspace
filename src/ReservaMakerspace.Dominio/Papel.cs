namespace ReservaMakerspace.Dominio;

/// <summary>
/// Papel de uma pessoa. Os papéis são cumulativos: Coordenação faz o que a Equipe faz,
/// e a Equipe faz o que o Usuário faz (spec §4.5).
/// </summary>
public enum Papel
{
    Usuario,
    Equipe,
    Coordenacao,
}
