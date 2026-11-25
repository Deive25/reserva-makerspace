namespace ReservaMakerspace.Dominio;

/// <summary>Uma máquina reservável. Desativar não apaga: o histórico de reservas continua (spec §4.6).</summary>
public class Equipamento
{
    public Guid Id { get; set; }

    public string Nome { get; set; } = "";

    public Guid TipoId { get; set; }

    public TipoDeEquipamento Tipo { get; set; } = null!;

    public bool Ativo { get; set; } = true;
}
