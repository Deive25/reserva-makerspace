using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ReservaMakerspace.Dominio;

namespace ReservaMakerspace.Api.Dados;

/// <summary>
/// Dados de exemplo do Itec Park, aplicados na subida quando <c>DadosDeExemplo:Aplicar</c> é verdadeiro
/// (ADR 0004). Só <b>cria</b>: nunca altera nem apaga. Os treinamentos e as reservas de exemplo entram só se ainda não houver nenhum(a).
/// </summary>
public static class DadosDeExemplo
{
    private sealed record TipoDeExemplo(string Nome, int Minutos, string[] Maquinas, string[]? Inativas = null);

    private sealed record PessoaDeExemplo(string Nome, string Email, Papel Papel);

    private static readonly TipoDeExemplo[] Tipos =
    [
        new("Cortadora a laser", 120, ["Cortadora a laser"]),
        new("Impressora 3D FDM", 720, ["Impressora FDM 1", "Impressora FDM 2", "Impressora FDM 3"]),
        new("Impressora 3D de resina", 480, ["Impressora de resina"]),
        // A máquina inativa existe para mostrar, na tela e nos testes, o caso da máquina fora de uso.
        new("Scanner 3D", 120, [], Inativas: ["Scanner 3D"]),
    ];

    // O domínio .example é reservado: nenhum e-mail enviado a ele chega a alguém.
    private const string EmailDaAna = "aluna@makerspace.example";
    private const string EmailDoBeto = "bolsista@makerspace.example";
    private const string EmailDaCarla = "coordenadora@makerspace.example";

    private static readonly PessoaDeExemplo[] Pessoas =
    [
        new("Ana Aluna", EmailDaAna, Papel.Usuario),
        new("Beto Bolsista", EmailDoBeto, Papel.Equipe),
        new("Carla Coordenadora", EmailDaCarla, Papel.Coordenacao),
    ];

    public static async Task AplicarAsync(
        MakerspaceDbContext banco,
        UserManager<Usuario> usuarios,
        IConfiguration configuracao,
        TimeProvider relogio,
        ILogger logger)
    {
        await CriarTiposEMaquinasAsync(banco, logger);
        await CriarPessoasAsync(usuarios, configuracao["DadosDeExemplo:Senha"], logger);
        await CriarTreinamentosAsync(banco, relogio, logger);
        await CriarReservasAsync(banco, relogio, logger);
    }

    // Só se não houver nenhum tipo: uma máquina renomeada pela Coordenação não volta com o nome antigo.
    private static async Task CriarTiposEMaquinasAsync(MakerspaceDbContext banco, ILogger logger)
    {
        if (await banco.TiposDeEquipamento.AnyAsync())
        {
            return;
        }

        foreach (var exemplo in Tipos)
        {
            var tipo = new TipoDeEquipamento
            {
                Nome = exemplo.Nome,
                DuracaoMaximaPessoal = TimeSpan.FromMinutes(exemplo.Minutos),
            };
            banco.TiposDeEquipamento.Add(tipo);
            foreach (var nome in exemplo.Maquinas)
            {
                banco.Equipamentos.Add(new Equipamento { Nome = nome, Tipo = tipo, Ativo = true });
            }

            foreach (var nome in exemplo.Inativas ?? [])
            {
                banco.Equipamentos.Add(new Equipamento { Nome = nome, Tipo = tipo, Ativo = false });
            }
        }

        await banco.SaveChangesAsync();
        logger.LogInformation("Tipos de equipamento e máquinas de exemplo criados.");
    }

    // Datas relativas ao "agora" da subida que os cria (plano do 1.4, decisão 12).
    private sealed record TreinamentoDeExemplo(
        string Pessoa,
        string Tipo,
        int MesesAtras,
        string RegistradoPor,
        int? RevogadoHaDias = null,
        string? Motivo = null);

    private const string TipoFdm = "Impressora 3D FDM";
    private const string TipoLaser = "Cortadora a laser";
    private const string TipoResina = "Impressora 3D de resina";
    private const string TipoScanner = "Scanner 3D";

    private static readonly TreinamentoDeExemplo[] Treinamentos =
    [
        new(EmailDaAna, TipoFdm, 2, EmailDoBeto),
        // Vencido: mostra a renovação pendente.
        new(EmailDaAna, TipoLaser, 13, EmailDoBeto),
        new(EmailDaAna, TipoResina, 3, EmailDoBeto, RevogadoHaDias: 7, Motivo: "Descarte de resina fora do recipiente."),
        new(EmailDoBeto, TipoLaser, 6, EmailDaCarla),
        new(EmailDoBeto, TipoFdm, 6, EmailDaCarla),
        new(EmailDoBeto, TipoResina, 6, EmailDaCarla),
        new(EmailDoBeto, TipoScanner, 6, EmailDaCarla),
        // Registro anterior à renovação: mostra o histórico.
        new(EmailDoBeto, TipoFdm, 18, EmailDaCarla),
        new(EmailDaCarla, TipoLaser, 1, EmailDoBeto),
    ];

    // Só se ainda não houver nenhum treinamento, e só para quem existe: uma pessoa sem conta ou um tipo
    // renomeado pela Coordenação fica de fora, sem erro.
    private static async Task CriarTreinamentosAsync(MakerspaceDbContext banco, TimeProvider relogio, ILogger logger)
    {
        if (await banco.Treinamentos.AnyAsync())
        {
            return;
        }

        var pessoas = await banco.Users.AsNoTracking()
            .Where(u => u.Email != null)
            .ToDictionaryAsync(u => u.Email!, u => u.Id);
        var tipos = await banco.TiposDeEquipamento.AsNoTracking().ToDictionaryAsync(t => t.Nome, t => t.Id);
        var agora = relogio.GetUtcNow();

        var criados = 0;
        foreach (var exemplo in Treinamentos)
        {
            if (!pessoas.TryGetValue(exemplo.Pessoa, out var pessoaId)
                || !pessoas.TryGetValue(exemplo.RegistradoPor, out var registradoPorId)
                || !tipos.TryGetValue(exemplo.Tipo, out var tipoId))
            {
                continue;
            }

            var treinamento = Treinamento.Registrar(pessoaId, tipoId, registradoPorId, agora.AddMonths(-exemplo.MesesAtras));
            if (exemplo.RevogadoHaDias is { } dias)
            {
                treinamento.Revogar(registradoPorId, exemplo.Motivo!, agora.AddDays(-dias));
            }

            banco.Treinamentos.Add(treinamento);
            criados++;
        }

        await banco.SaveChangesAsync();
        logger.LogInformation("Treinamentos de exemplo criados: {Total}.", criados);
    }

    // Os horários são horas cheias de Brasília (UTC−3, sem horário de verão desde 2019), contadas a partir do dia da
    // subida. A reserva passa pelo Reserva.Criar com o "agora" de quando teria sido feita (CriadaHaDias), como os
    // treinamentos acima (plano do 1.5, decisão 13).
    private sealed record CancelamentoDeExemplo(SituacaoDaReserva Situacao, string Autor, string? Motivo, int HaDias);

    private sealed record ReservaDeExemplo(
        string Pessoa,
        string Maquina,
        int DiasDeHoje,
        int HoraInicio,
        int HoraFim,
        FinalidadeDaReserva Finalidade,
        int CriadaHaDias,
        string? Solicitante = null,
        string? Descricao = null,
        CancelamentoDeExemplo? Cancelamento = null);

    private static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);

    private static readonly ReservaDeExemplo[] Reservas =
    [
        new(EmailDaAna, "Impressora FDM 1", 1, 9, 13, FinalidadeDaReserva.ProjetoPessoal, 0, Descricao: "Suporte de celular"),
        // Já passou: o histórico.
        new(EmailDaAna, "Impressora FDM 3", -1, 14, 18, FinalidadeDaReserva.ProjetoPessoal, 2),
        // Cancelada pela revogação do treinamento de resina da Ana (a de exemplo do 1.4, há 7 dias).
        new(EmailDaAna, "Impressora de resina", -5, 10, 12, FinalidadeDaReserva.ProjetoPessoal, 8,
            Cancelamento: new(SituacaoDaReserva.CanceladaPelaEquipe, EmailDoBeto, "Treinamento revogado", 7)),
        new(EmailDaAna, "Impressora FDM 2", 2, 10, 14, FinalidadeDaReserva.ProjetoPessoal, 0,
            Cancelamento: new(SituacaoDaReserva.CanceladaPeloDono, EmailDaAna, null, 0)),
        // 4 h na cortadora: acima das 2 h do projeto pessoal, que a demanda do parque não tem.
        new(EmailDoBeto, "Cortadora a laser", 1, 14, 18, FinalidadeDaReserva.DemandaDoParque, 0,
            Solicitante: "Prof. Daniel (TCC de Engenharia)"),
        new(EmailDoBeto, "Impressora de resina", 3, 8, 16, FinalidadeDaReserva.DemandaDoParque, 0),
        new(EmailDaCarla, "Cortadora a laser", 2, 10, 11, FinalidadeDaReserva.ProjetoPessoal, 0),
    ];

    // Só se ainda não houver nenhuma reserva, e só para quem existe: uma pessoa sem conta, uma máquina renomeada ou um
    // treinamento que falta deixam a reserva de fora (a regra do domínio a recusa), sem erro.
    private static async Task CriarReservasAsync(MakerspaceDbContext banco, TimeProvider relogio, ILogger logger)
    {
        if (await banco.Reservas.AnyAsync())
        {
            return;
        }

        var pessoas = await banco.Users.AsNoTracking()
            .Where(u => u.Email != null)
            .ToDictionaryAsync(u => u.Email!);
        var maquinas = await banco.Equipamentos.AsNoTracking().Include(e => e.Tipo).ToDictionaryAsync(e => e.Nome);
        var treinamentos = await banco.Treinamentos.AsNoTracking().ToListAsync();
        var agora = relogio.GetUtcNow();
        var hoje = agora.ToOffset(Brasilia).Date;

        var criadas = new List<Reserva>();
        foreach (var exemplo in Reservas)
        {
            if (!pessoas.TryGetValue(exemplo.Pessoa, out var pessoa) || !maquinas.TryGetValue(exemplo.Maquina, out var maquina))
            {
                continue;
            }

            var inicio = new DateTimeOffset(hoje.AddDays(exemplo.DiasDeHoje).AddHours(exemplo.HoraInicio), Brasilia);
            var fim = new DateTimeOffset(hoje.AddDays(exemplo.DiasDeHoje).AddHours(exemplo.HoraFim), Brasilia);
            var criadaEm = agora.AddDays(-exemplo.CriadaHaDias);

            // O que a API leria do banco naquele momento: os treinamentos ainda não revogados e as pessoais ainda em pé.
            var treinadoAte = treinamentos
                .Where(t => t.UsuarioId == pessoa.Id && t.TipoId == maquina.TipoId
                    && t.RealizadoEm <= criadaEm && (t.RevogadoEm is null || t.RevogadoEm > criadaEm))
                .Max(t => (DateTimeOffset?)t.ValidoAte);
            var pessoaisAtivas = criadas.Count(r =>
                r.OperadorId == pessoa.Id
                && r.Finalidade == FinalidadeDaReserva.ProjetoPessoal
                && r.CriadaEm <= criadaEm
                && r.Fim > criadaEm
                && (r.CanceladaEm is null || r.CanceladaEm > criadaEm));

            Reserva reserva;
            try
            {
                reserva = Reserva.Criar(
                    maquina, pessoa.Id, pessoa.Papel, exemplo.Finalidade, inicio, fim,
                    exemplo.Solicitante, exemplo.Descricao, treinadoAte, pessoaisAtivas, criadaEm);
            }
            catch (ReservaRecusadaException erro)
            {
                logger.LogInformation("Reserva de exemplo de {Pessoa} em {Maquina} ficou de fora: {Motivo}", exemplo.Pessoa, exemplo.Maquina, erro.Message);
                continue;
            }

            if (exemplo.Cancelamento is { } cancelamento && pessoas.TryGetValue(cancelamento.Autor, out var autor))
            {
                reserva.Cancelar(cancelamento.Situacao, autor.Id, cancelamento.Motivo, agora.AddDays(-cancelamento.HaDias));
            }

            criadas.Add(reserva);
            banco.Reservas.Add(reserva);
        }

        await banco.SaveChangesAsync();
        logger.LogInformation("Reservas de exemplo criadas: {Total}.", criadas.Count);
    }

    // Cada pessoa pelo e-mail: um papel ou uma senha que mudaram depois continuam como estão.
    private static async Task CriarPessoasAsync(UserManager<Usuario> usuarios, string? senha, ILogger logger)
    {
        if (string.IsNullOrEmpty(senha))
        {
            logger.LogWarning(
                "DadosDeExemplo__Senha não foi definida: os tipos e as máquinas de exemplo entram, mas as pessoas de exemplo não.");
            return;
        }

        foreach (var pessoa in Pessoas)
        {
            if (await usuarios.FindByEmailAsync(pessoa.Email) is not null)
            {
                continue;
            }

            var usuario = new Usuario
            {
                UserName = pessoa.Email,
                Email = pessoa.Email,
                Nome = pessoa.Nome,
                Papel = pessoa.Papel,
            };
            var resultado = await usuarios.CreateAsync(usuario, senha);
            if (!resultado.Succeeded)
            {
                var motivos = string.Join(" ", resultado.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Não foi possível criar a pessoa de exemplo {pessoa.Email}: {motivos}");
            }
        }

        logger.LogInformation("Pessoas de exemplo conferidas.");
    }
}
