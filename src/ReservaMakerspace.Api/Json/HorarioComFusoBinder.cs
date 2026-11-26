using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ReservaMakerspace.Api.Json;

/// <summary>
/// Lê um horário da query pela mesma regra do corpo (<see cref="HorarioComFuso"/>). Um binder, e não um
/// <c>string</c> convertido na ação: o 400 sai do próprio <c>ModelState</c>, junto com os outros erros de formato e
/// no nome do campo, e a ação só roda com horários válidos. Parâmetro ausente não é erro aqui: o
/// <c>[Required]</c> do parâmetro cuida disso, como nos corpos.
/// </summary>
public sealed class HorarioComFusoBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext contexto)
    {
        var valor = contexto.ValueProvider.GetValue(contexto.ModelName);
        if (valor == ValueProviderResult.None || string.IsNullOrEmpty(valor.FirstValue))
        {
            return Task.CompletedTask;
        }

        contexto.ModelState.SetModelValue(contexto.ModelName, valor);
        // O Npgsql só grava e compara timestamptz com offset 0: o horário entra na consulta em UTC.
        if (HorarioComFuso.Tentar(valor.FirstValue, out var horario))
        {
            contexto.Result = ModelBindingResult.Success(horario.ToUniversalTime());
        }
        else
        {
            contexto.ModelState.TryAddModelError(contexto.ModelName, HorarioComFuso.Mensagem);
        }

        return Task.CompletedTask;
    }
}
