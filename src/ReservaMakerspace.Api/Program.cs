using Microsoft.EntityFrameworkCore;
using ReservaMakerspace.Api.Dados;
using ReservaMakerspace.Api.Erros;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// A connection string não tem valor padrão: sem ela a API nem sobe, e a mensagem diz o que configurar.
var connectionString = builder.Configuration.GetConnectionString("Makerspace");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Configure a connection string ConnectionStrings__Makerspace.");
}

builder.Services.AddDbContext<MakerspaceDbContext>(opcoes => opcoes.UseNpgsql(connectionString));

builder.Services.AddHealthChecks().AddDbContextCheck<MakerspaceDbContext>();

builder.Services.AddProblemDetails(opcoes =>
    opcoes.CustomizeProblemDetails = contexto =>
    {
        var titulo = TitulosDeErro.Para(contexto.ProblemDetails.Status ?? contexto.HttpContext.Response.StatusCode);
        if (titulo is not null)
        {
            contexto.ProblemDetails.Title = titulo;
        }
    });

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/saude");
app.MapControllers();

// Uma instância só: aplicar as migrações aqui é suficiente (ADR 0002).
if (app.Configuration.GetValue("Banco:AplicarMigracoesNaSubida", true))
{
    await using var escopo = app.Services.CreateAsyncScope();
    var banco = escopo.ServiceProvider.GetRequiredService<MakerspaceDbContext>();
    await banco.Database.MigrateAsync();
}

app.Run();

public partial class Program;
