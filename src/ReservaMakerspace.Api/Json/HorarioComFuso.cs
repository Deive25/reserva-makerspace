using System.Globalization;
using System.Text.RegularExpressions;

namespace ReservaMakerspace.Api.Json;

/// <summary>
/// A regra do horário com fuso, num lugar só: o corpo (<see cref="ConversorDeDataComFuso"/>) e a query
/// (<see cref="HorarioComFusoBinder"/>) a usam. O texto precisa trazer <c>Z</c> ou <c>±hh:mm</c>; sem ele o .NET
/// leria o horário no fuso do servidor, que muda de máquina para máquina (plano do 1.5, decisão 11).
/// </summary>
public static partial class HorarioComFuso
{
    public const string Mensagem =
        "Use o formato ISO 8601 com o fuso, como 2026-12-01T10:00:00-03:00 ou 2026-12-01T13:00:00Z.";

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T[\d:.,]+(Z|z|[+-]\d{2}:\d{2})$")]
    private static partial Regex DataComHoraEFuso();

    public static bool TemFuso(string? texto) => texto is not null && DataComHoraEFuso().IsMatch(texto);

    /// <summary>Lê o texto só se ele trouxer o fuso. Na URL o <c>+</c> chega como espaço: use <c>Z</c> ou <c>%2B</c>.</summary>
    public static bool Tentar(string? texto, out DateTimeOffset horario)
    {
        horario = default;
        return TemFuso(texto)
            && DateTimeOffset.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.None, out horario);
    }
}
