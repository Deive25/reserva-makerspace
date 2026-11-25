using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ReservaMakerspace.Api.Dados;

/// <summary>
/// <c>IdentityUserContext</c> traz as tabelas de usuários, mas não as de papéis do Identity:
/// aqui cada pessoa tem exatamente um papel, guardado em <see cref="Usuario.Papel"/> (ADR 0003).
/// </summary>
public class MakerspaceDbContext(DbContextOptions<MakerspaceDbContext> opcoes)
    : IdentityUserContext<Usuario, Guid>(opcoes)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Necessária para a restrição de exclusão das reservas (incremento 1.5).
        modelBuilder.HasPostgresExtension("btree_gist");

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
