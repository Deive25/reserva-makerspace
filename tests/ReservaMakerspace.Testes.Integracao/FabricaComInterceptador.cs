using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ReservaMakerspace.Api.Dados;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// Um interceptador de comandos do EF Core com dois ganchos para a desmarcação (plano do 1.6, decisões 6 e 7). Armado,
/// faz falhar todo <c>INSERT</c> em <c>reservas</c>: o <c>UPDATE</c> que cancela as pessoais já rodou quando o <c>INSERT</c> da
/// demanda falha, e a falha precisa desfazer os dois. E roda uma ação logo antes do <c>UPDATE</c> da desmarcação, para o
/// teste cancelar a reserva como o dono, de forma determinística, no meio do pedido.
/// </summary>
public sealed class InterceptadorDeReservas : DbCommandInterceptor
{
    private volatile bool _armado;
    private Func<Task>? _antesDoUpdate;

    public void ArmarFalhaNoInsert() => _armado = true;

    public void DesarmarFalhaNoInsert() => _armado = false;

    /// <summary>Roda a ação uma vez, logo antes do próximo <c>UPDATE</c> em <c>reservas</c> (o da desmarcação).</summary>
    public void AntesDoProximoUpdate(Func<Task> acao) => _antesDoUpdate = acao;

    private void Conferir(DbCommand comando)
    {
        if (_armado && comando.CommandText.Contains("INSERT INTO reservas", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Falha forçada no INSERT em reservas (teste da transação única).");
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand comando, CommandEventData dados, InterceptionResult<DbDataReader> resultado)
    {
        Conferir(comando);
        return base.ReaderExecuting(comando, dados, resultado);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand comando,
        CommandEventData dados,
        InterceptionResult<DbDataReader> resultado,
        CancellationToken cancellationToken = default)
    {
        Conferir(comando);
        return base.ReaderExecutingAsync(comando, dados, resultado, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand comando, CommandEventData dados, InterceptionResult<int> resultado)
    {
        Conferir(comando);
        return base.NonQueryExecuting(comando, dados, resultado);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand comando,
        CommandEventData dados,
        InterceptionResult<int> resultado,
        CancellationToken cancellationToken = default)
    {
        Conferir(comando);
        if (comando.CommandText.StartsWith("UPDATE reservas", StringComparison.Ordinal)
            && Interlocked.Exchange(ref _antesDoUpdate, null) is { } acao)
        {
            await acao();
        }

        return await base.NonQueryExecutingAsync(comando, dados, resultado, cancellationToken);
    }
}

/// <summary>
/// Sobe a API num banco próprio com o <see cref="InterceptadorDeReservas"/> registrado por
/// <c>ConfigureDbContext</c>: o teste monta o cenário com os ganchos desligados e os liga só no pedido que quer observar.
/// Fica fora da coleção compartilhada, como a <see cref="FabricaComRelogio"/>. Sem pacote novo.
/// </summary>
public sealed class FabricaComInterceptador(string connectionString) : WebApplicationFactory<Program>
{
    private readonly string _chave = ConfiguracaoDeTeste.NovaChave();
    private readonly string _coordenacaoEmail = AuxiliarDeContas.NovoEmail();
    private readonly string _coordenacaoSenha = AuxiliarDeContas.NovaSenha();
    private AuxiliarDeContas? _contas;

    public string ConnectionString { get; } = connectionString;

    public InterceptadorDeReservas Interceptador { get; } = new();

    /// <summary>Cadastra, promove e faz login de pessoas de teste.</summary>
    public AuxiliarDeContas Contas => _contas ??= new AuxiliarDeContas(this, _coordenacaoEmail, _coordenacaoSenha);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (chave, valor) in ConfiguracaoDeTeste.Para(ConnectionString, _chave, _coordenacaoEmail, _coordenacaoSenha))
        {
            if (valor is not null)
            {
                builder.UseSetting(chave, valor);
            }
        }

        // Acrescenta o interceptador à configuração do contexto que o Program já registrou.
        builder.ConfigureTestServices(servicos =>
            servicos.ConfigureDbContext<MakerspaceDbContext>(opcoes => opcoes.AddInterceptors(Interceptador)));
    }
}
