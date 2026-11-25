using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Api.Dados;

/// <summary>
/// <c>IdentityUserContext</c> traz as tabelas de usuários, mas não as de papéis do Identity:
/// aqui cada pessoa tem exatamente um papel, guardado em <see cref="Usuario.Papel"/> (ADR 0003).
/// </summary>
public class MakerspaceDbContext(DbContextOptions<MakerspaceDbContext> opcoes)
    : IdentityUserContext<Usuario, Guid>(opcoes)
{
    /// <summary>Nomes de tipo e de máquina que só diferem na caixa são o mesmo nome; os acentos contam.</summary>
    public const string ColacaoDoNome = "nome_sem_diferenciar_caixa";

    public const string IndiceDoNomeDoTipo = "ix_tipos_de_equipamento_nome";

    public const string IndiceDoNomeDaMaquina = "ix_equipamentos_nome";

    public DbSet<TipoDeEquipamento> TiposDeEquipamento => Set<TipoDeEquipamento>();

    public DbSet<Equipamento> Equipamentos => Set<Equipamento>();

    public DbSet<Treinamento> Treinamentos => Set<Treinamento>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Necessária para a restrição de exclusão das reservas (incremento 1.5).
        modelBuilder.HasPostgresExtension("btree_gist");

        // Colação ICU não determinística, nível 2: ignora a caixa e considera os acentos (decisão 4 do plano do 1.3).
        modelBuilder.HasCollation(ColacaoDoNome, locale: "und-u-ks-level2", provider: "icu", deterministic: false);

        modelBuilder.Entity<TipoDeEquipamento>(tipo =>
        {
            tipo.ToTable("tipos_de_equipamento");
            tipo.HasKey(t => t.Id);
            tipo.Property(t => t.Nome).HasMaxLength(100).UseCollation(ColacaoDoNome);
            tipo.HasIndex(t => t.Nome).IsUnique().HasDatabaseName(IndiceDoNomeDoTipo);
        });

        modelBuilder.Entity<Equipamento>(maquina =>
        {
            maquina.ToTable("equipamentos");
            maquina.HasKey(e => e.Id);
            maquina.Property(e => e.Nome).HasMaxLength(100).UseCollation(ColacaoDoNome);
            maquina.HasIndex(e => e.Nome).IsUnique().HasDatabaseName(IndiceDoNomeDaMaquina);
            maquina.HasOne(e => e.Tipo).WithMany().HasForeignKey(e => e.TipoId).OnDelete(DeleteBehavior.Restrict);
        });

        // O Treinamento mora no Dominio, que não conhece o Usuario (do Identity, na Api): por isso as chaves
        // estrangeiras para usuarios são declaradas aqui. Nenhuma tem exclusão em cascata: o histórico fica.
        modelBuilder.Entity<Treinamento>(treinamento =>
        {
            treinamento.ToTable("treinamentos");
            treinamento.HasKey(t => t.Id);
            treinamento.Property(t => t.MotivoDaRevogacao).HasMaxLength(500);
            // Atende "treinamentos da pessoa no tipo": a revogação, o /me e, no 1.5, a regra de quem pode reservar.
            treinamento.HasIndex(t => new { t.UsuarioId, t.TipoId });
            treinamento.HasOne<Usuario>().WithMany().HasForeignKey(t => t.UsuarioId).OnDelete(DeleteBehavior.Restrict);
            treinamento.HasOne<TipoDeEquipamento>().WithMany().HasForeignKey(t => t.TipoId).OnDelete(DeleteBehavior.Restrict);
            treinamento.HasOne<Usuario>().WithMany().HasForeignKey(t => t.RegistradoPorId).OnDelete(DeleteBehavior.Restrict);
            treinamento.HasOne<Usuario>().WithMany().HasForeignKey(t => t.RevogadoPorId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Usuario>(usuario =>
        {
            usuario.ToTable("usuarios");
            usuario.Property(u => u.Nome).HasMaxLength(100);
            usuario.Property(u => u.Papel).HasConversion<string>().HasMaxLength(20);
        });
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("usuarios_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("usuarios_logins");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("usuarios_tokens");
    }
}
