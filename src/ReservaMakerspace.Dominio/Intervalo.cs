namespace ReservaMakerspace.Dominio;

/// <summary>
/// Intervalo de tempo semiaberto [Inicio, Fim): quem termina às 10:00 não conflita com quem começa às 10:00.
/// Compara instantes (<see cref="DateTimeOffset"/>), então fusos diferentes não mudam o resultado.
/// </summary>
public sealed record Intervalo
{
    public Intervalo(DateTimeOffset inicio, DateTimeOffset fim)
    {
        if (fim <= inicio)
        {
            throw new ArgumentException("O fim precisa ser depois do início.", nameof(fim));
        }

        Inicio = inicio;
        Fim = fim;
    }

    public DateTimeOffset Inicio { get; }

    public DateTimeOffset Fim { get; }

    public TimeSpan Duracao => Fim - Inicio;

    public bool SeSobrepoeA(Intervalo outro) =>
        Inicio < outro.Fim && outro.Inicio < Fim;
}
