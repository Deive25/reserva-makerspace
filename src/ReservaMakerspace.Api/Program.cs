using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using ReservaMakerspace.Api.Autenticacao;
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

// O mesmo vale para a chave do JWT (depois da connection string, para não mudar a mensagem anterior).
var jwt = OpcoesJwt.Ler(builder.Configuration);

builder.Services.AddDbContext<MakerspaceDbContext>(opcoes =>
    opcoes.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

builder.Services.AddAutenticacao(jwt);

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

builder.Services
    // As chaves de `errors` seguem o nome do JSON (errors.senha), e não o da propriedade C# (Senha).
    .AddControllers(opcoes => opcoes.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider()))
    // Enums como texto ("Coordenacao"); número no lugar do nome é recusado.
    .AddJsonOptions(opcoes => opcoes.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

builder.Services.AddOpenApi(opcoes => opcoes.AddDocumentTransformer((documento, _, _) =>
{
    // Declara o esquema Bearer (JWT): o Scalar mostra o campo do token e o envia nas chamadas.
    documento.Components ??= new OpenApiComponents();
    documento.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
    documento.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
    };
    documento.Security ??= [];
    documento.Security.Add(new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", documento)] = [],
    });
    return Task.CompletedTask;
}));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

// Toda rota exige login (FallbackPolicy). Só as públicas são marcadas.
app.MapOpenApi().AllowAnonymous();
app.MapScalarApiReference().AllowAnonymous();
app.MapHealthChecks("/saude").AllowAnonymous();
app.MapControllers();

// Uma instância só: aplicar as migrações aqui é suficiente (ADR 0002).
if (app.Configuration.GetValue("Banco:AplicarMigracoesNaSubida", true))
{
    await using var escopo = app.Services.CreateAsyncScope();
    var servicos = escopo.ServiceProvider;
    var banco = servicos.GetRequiredService<MakerspaceDbContext>();
    await banco.Database.MigrateAsync();

    await CoordenacaoInicial.AplicarAsync(
        banco,
        servicos.GetRequiredService<UserManager<Usuario>>(),
        app.Configuration,
        app.Logger);
}

app.Run();

public partial class Program;
