namespace ReservaMakerspace.Dominio;

/// <summary>
/// Categoria de máquina (impressora 3D, cortadora a laser...). O treinamento vale para o tipo,
/// e a duração máxima de uma reserva pessoal também (spec §4.1).
/// </summary>
public class TipoDeEquipamento
{
    public Guid Id { get; set; }

    public string Nome { get; set; } = "";

    public TimeSpan DuracaoMaximaPessoal { get; set; }
}
