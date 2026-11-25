using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace ReservaMakerspace.Testes.Integracao;

/// <summary>
/// Sobe a API num banco próprio, com um <see cref="FakeTimeProvider"/> no lugar do <see cref="TimeProvider"/> real.
/// O teste avança o relógio (meses, minutos) e a API inteira, inclusive a validação do JWT, enxerga o mesmo tempo.
/// Fica fora da coleção compartilhada para não mudar o relógio dos outros testes. Custa mais: um banco por teste.
/// </summary>
public sealed class FabricaComRelogio : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _chave = ConfiguracaoDeTeste.NovaChave();
    private readonly string _coordenacaoEmail = AuxiliarDeContas.NovoEmail();
    private readonly string _coordenacaoSenha = AuxiliarDeContas.NovaSenha();
    private AuxiliarDeContas? _contas;

    public FabricaComRelogio(string connectionString, FakeTimeProvider relogio)
    {
        _connectionString = connectionString;
        Relogio = relogio;
    }

    public FakeTimeProvider Relogio { get; }

    /// <summary>Cadastra, promove e faz login de pessoas de teste.</summary>
    public AuxiliarDeContas Contas => _contas ??= new AuxiliarDeContas(this, _coordenacaoEmail, _coordenacaoSenha);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (chave, valor) in ConfiguracaoDeTeste.Para(_connectionString, _chave, _coordenacaoEmail, _coordenacaoSenha))
        {
            if (valor is not null)
            {
                builder.UseSetting(chave, valor);
            }
        }

        builder.ConfigureTestServices(servicos =>
        {
            servicos.RemoveAll<TimeProvider>();
            servicos.AddSingleton<TimeProvider>(Relogio);
        });
    }
}
