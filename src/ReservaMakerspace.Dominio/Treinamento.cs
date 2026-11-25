namespace ReservaMakerspace.Dominio;

/// <summary>
/// Treinamento de uma pessoa num tipo de equipamento (spec §4.1). Vale por 12 meses a partir do registro,
/// no intervalo semiaberto [RealizadoEm, ValidoAte). Renovar é registrar outro; o histórico não se apaga.
/// O "agora" sempre chega por parâmetro (vem do <see cref="TimeProvider"/> da API), para a regra ser testável.
/// </summary>
public class Treinamento
{
    public const int MesesDeValidade = 12;

    // Construtor para o EF Core materializar a linha; o resto do código usa Registrar.
    private Treinamento()
    {
    }

    public Guid Id { get; private set; }

    public Guid UsuarioId { get; private set; }

    public Guid TipoId { get; private set; }

    public Guid RegistradoPorId { get; private set; }

    public DateTimeOffset RealizadoEm { get; private set; }

    /// <summary>Gravada na linha (e não calculada na consulta): o 1.5 a compara com o fim da reserva direto no SQL.</summary>
    public DateTimeOffset ValidoAte { get; private set; }

    public DateTimeOffset? RevogadoEm { get; private set; }

    public Guid? RevogadoPorId { get; private set; }

    public string? MotivoDaRevogacao { get; private set; }

    public static Treinamento Registrar(Guid usuarioId, Guid tipoId, Guid registradoPorId, DateTimeOffset agora) =>
        new()
        {
            Id = Guid.NewGuid(),
            UsuarioId = usuarioId,
            TipoId = tipoId,
            RegistradoPorId = registradoPorId,
            RealizadoEm = agora,
            ValidoAte = agora.AddMonths(MesesDeValidade),
        };

    /// <summary>A revogação prevalece sobre o vencimento.</summary>
    public SituacaoDoTreinamento SituacaoEm(DateTimeOffset agora)
    {
        if (RevogadoEm is not null)
        {
            return SituacaoDoTreinamento.Revogado;
        }

        return agora < ValidoAte ? SituacaoDoTreinamento.Valido : SituacaoDoTreinamento.Vencido;
    }

    /// <summary>Só um treinamento válido naquele instante pode ser revogado.</summary>
    public void Revogar(Guid revogadoPorId, string motivo, DateTimeOffset agora)
    {
        if (string.IsNullOrWhiteSpace(motivo))
        {
            throw new ArgumentException("Informe o motivo da revogação.", nameof(motivo));
        }

        switch (SituacaoEm(agora))
        {
            case SituacaoDoTreinamento.Revogado:
                throw new InvalidOperationException("O treinamento já foi revogado.");
            case SituacaoDoTreinamento.Vencido:
                throw new InvalidOperationException("O treinamento já venceu.");
        }

        RevogadoEm = agora;
        RevogadoPorId = revogadoPorId;
        MotivoDaRevogacao = motivo.Trim();
    }
}
