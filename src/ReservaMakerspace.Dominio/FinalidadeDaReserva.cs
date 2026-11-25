namespace ReservaMakerspace.Dominio;

/// <summary>Para que a máquina é reservada (spec §4.1). Sai como texto no JSON e no banco.</summary>
public enum FinalidadeDaReserva
{
    ProjetoPessoal,
    DemandaDoParque,
}
