using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReservaMakerspace.Api.Json;

/// <summary>
/// Lê um <see cref="DateTimeOffset"/> só se o texto trouxer o fuso (<c>Z</c> ou <c>±hh:mm</c>). Sem ele o
/// System.Text.Json assume o fuso do servidor, que muda de máquina para máquina (UTC no container, −03:00 no
/// desenvolvimento), e a mesma reserva viraria dois horários diferentes (plano do 1.5, decisão 11). Vale também para
/// <c>DateTimeOffset?</c>. A escrita é a padrão, para as respostas das rotas anteriores não mudarem.
/// </summary>
public sealed class ConversorDeDataComFuso : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type tipo, JsonSerializerOptions opcoes)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(HorarioComFuso.Mensagem);
        }

        var texto = reader.GetString();
        if (texto is null || !HorarioComFuso.TemFuso(texto))
        {
            throw new JsonException(HorarioComFuso.Mensagem);
        }

        try
        {
            return reader.GetDateTimeOffset();
        }
        catch (FormatException)
        {
            throw new JsonException(HorarioComFuso.Mensagem);
        }
    }

    public override void Write(Utf8JsonWriter escritor, DateTimeOffset valor, JsonSerializerOptions opcoes) =>
        escritor.WriteStringValue(valor);
}
