using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ReservaMakerspace.Api.Json;

/// <summary>
/// Lê um <see cref="DateTimeOffset"/> só se o texto trouxer o fuso (<c>Z</c> ou <c>±hh:mm</c>). Sem ele o
/// System.Text.Json assume o fuso do servidor, que muda de máquina para máquina (UTC no container, −03:00 no
/// desenvolvimento), e a mesma reserva viraria dois horários diferentes (plano do 1.5, decisão 11). Vale também para
/// <c>DateTimeOffset?</c>. A escrita é a padrão, para as respostas das rotas anteriores não mudarem.
/// </summary>
public sealed partial class ConversorDeDataComFuso : JsonConverter<DateTimeOffset>
{
    private const string Mensagem = "Use o formato ISO 8601 com o fuso, como 2026-12-01T10:00:00-03:00 ou 2026-12-01T13:00:00Z.";

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T[\d:.,]+(Z|z|[+-]\d{2}:\d{2})$")]
    private static partial Regex DataComHoraEFuso();

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type tipo, JsonSerializerOptions opcoes)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(Mensagem);
        }

        var texto = reader.GetString();
        if (texto is null || !DataComHoraEFuso().IsMatch(texto))
        {
            throw new JsonException(Mensagem);
        }

        try
        {
            return reader.GetDateTimeOffset();
        }
        catch (FormatException)
        {
            throw new JsonException(Mensagem);
        }
    }

    public override void Write(Utf8JsonWriter escritor, DateTimeOffset valor, JsonSerializerOptions opcoes) =>
        escritor.WriteStringValue(valor);
}
