using Microsoft.EntityFrameworkCore;

namespace ReservaMakerspace.Api.Dados;

public class MakerspaceDbContext(DbContextOptions<MakerspaceDbContext> opcoes) : DbContext(opcoes)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Necessária para a restrição de exclusão das reservas (incremento 1.5).
        modelBuilder.HasPostgresExtension("btree_gist");
    }
}
