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
/// Um interceptador de comandos do EF Core com ganchos para a desmarcação (plano do 1.6, decisões 6 e 7) e para o registro de manutenção (plano do 1.7). Armado,
/// faz falhar todo <c>INSERT</c> em <c>reservas</c>: o <c>UPDATE</c> que cancela as pessoais já rodou quando o <c>INSERT</c> da
/// demanda falha, e a falha precisa desfazer os dois. E roda uma ação logo antes de a desmarcação travar as linhas que vai cancelar, para o
/// teste cancelar a reserva como o dono, de forma determinística, no meio do pedido.
/// Outro gancho roda uma ação logo antes do <c>INSERT</c> da manutenção, para o teste gravar por SQL, numa conexão à parte, uma manutenção
/// sobreposta no meio do pedido e exercer a restrição de exclusão pela API.
/// </summary>
public sealed class InterceptadorDeReservas : DbCommandInterceptor
{
    private volatile bool _armado;
    private volatile bool _armadoNaManutencao;
    private Func<Task>? _antesDoUpdate;
    private Func<Task>? _antesDoInsertDeManutencao;

    public void ArmarFalhaNoInsert() => _armado = true;

    public void DesarmarFalhaNoInsert() => _armado = false;

    /// <summary>Armado, faz falhar todo <c>INSERT</c> em <c>manutencoes</c> (plano do 1.7: o registro cancela as reservas e grava a manutenção na mesma transação).</summary>
    public void ArmarFalhaNoInsertDeManutencao() => _armadoNaManutencao = true;

    public void DesarmarFalhaNoInsertDeManutencao() => _armadoNaManutencao = false;

    /// <summary>Roda a ação uma vez, logo antes de a desmarcação travar as linhas das reservas (o <c>SELECT ... FOR UPDATE</c> que vem antes do <c>UPDATE</c>).</summary>
    public void AntesDoProximoUpdate(Func<Task> acao) => _antesDoUpdate = acao;

    /// <summary>Roda a ação uma vez, logo antes do primeiro <c>INSERT INTO manutencoes</c>, depois de o registro ter travado a máquina e cancelado as reservas.</summary>
    public void AntesDoInsertDeManutencao(Func<Task> acao) => _antesDoInsertDeManutencao = acao;

    private async Task RodarGanchoDoInsertDeManutencaoAsync(DbCommand comando)
    {
        if (comando.CommandText.Contains("INSERT INTO manutencoes", StringComparison.Ordinal)
            && Interlocked.Exchange(ref _antesDoInsertDeManutencao, null) is { } acao)
        {
            await acao();
        }
    }

    private void Conferir(DbCommand comando)
    {
        if (_armado && comando.CommandText.Contains("INSERT INTO reservas", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Falha forçada no INSERT em reservas (teste da transação única).");
        }

        if (_armadoNaManutencao && comando.CommandText.Contains("INSERT INTO manutencoes", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Falha forçada no INSERT em manutencoes (teste da transação única).");
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand comando, CommandEventData dados, InterceptionResult<DbDataReader> resultado)
    {
        Conferir(comando);
        return base.ReaderExecuting(comando, dados, resultado);
    }

public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand comando,
        CommandEventData dados,
        InterceptionResult<DbDataReader> resultado,
        CancellationToken cancellationToken = default)
    {
        Conferir(comando);
        await RodarGanchoDoInsertDeManutencaoAsync(comando);
        // A trava das linhas da desmarcação (SELECT ... FOR UPDATE), logo antes de o pedido tomá-las.
        if (comando.CommandText.Contains("FROM reservas WHERE id = ANY", StringComparison.Ordinal)
            && Interlocked.Exchange(ref _antesDoUpdate, null) is { } acao)
        {
            await acao();
        }

        return await base.ReaderExecutingAsync(comando, dados, resultado, cancellationToken);
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
        await RodarGanchoDoInsertDeManutencaoAsync(comando);
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
