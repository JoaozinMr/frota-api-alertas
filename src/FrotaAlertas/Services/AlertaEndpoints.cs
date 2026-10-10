using FrotaAlertas.Models;
using FrotaAlertas.Options;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog.Context;

namespace FrotaAlertas.Services;

/// <summary>
/// Endpoints REST de alertas. Regras de status para o Worker:
/// 201 novo, 200 duplicata (nunca 409), 400 só para payload inválido,
/// 503 para indisponibilidade (transitório: o Worker reprocessa sem DLQ).
/// </summary>
public static class AlertaEndpoints
{
    private const int TopVeiculosPadrao = 10;
    private const int TopVeiculosMax = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alerts");

        group.MapPost("/", CriarAsync);
        group.MapGet("/", ListarAsync);
        group.MapGet("/resumo", ResumoAsync);
        group.MapGet("/{id:long}", ObterAsync);
    }

    internal static async Task<IResult> CriarAsync(
        AlertaRequest? request,
        HttpContext http,
        IAlertaRepository repository,
        SchemaState schema,
        IOptions<AlertasOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("FrotaAlertas.Alertas");
        var correlationId = CorrelationId.Obter(http);

        var resultado = AlertaValidator.Validar(request, correlationId);
        if (!resultado.Valido)
        {
            logger.LogWarning("Alerta rejeitado por payload inválido: {@Erros}", resultado.Erros);
            return Results.ValidationProblem(resultado.Erros);
        }

        var alerta = resultado.Alerta!;
        using var _ = LogContext.PushProperty("IdVeiculo", alerta.IdVeiculo);
        using var __ = LogContext.PushProperty("Tipo", alerta.TipoInfracao);

        if (!schema.Pronto)
        {
            logger.LogWarning("Alerta recusado: schema ainda não inicializado");
            return Indisponivel("Banco de dados ainda inicializando.");
        }

        // Orçamento abaixo do timeout de 3s do Worker: melhor responder 503 do que deixá-lo esperar.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(100, options.Value.RequestTimeoutMs)));

        try
        {
            var (salvo, criado) = await repository.InserirAsync(alerta, cts.Token);

            if (criado)
            {
                logger.LogInformation("Alerta {AlertaId} registrado: {Descricao}", salvo.Id, salvo.Descricao);
                return Results.Created($"/api/alerts/{salvo.Id}", salvo);
            }

            logger.LogInformation("Alerta duplicado ignorado (idempotente); existente {AlertaId}", salvo.Id);
            return Results.Ok(salvo);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Timeout ao persistir alerta ({TimeoutMs} ms)", options.Value.RequestTimeoutMs);
            return Indisponivel("Timeout ao persistir o alerta.");
        }
        catch (NpgsqlException ex)
        {
            logger.LogError(ex, "Falha no PostgreSQL ao persistir alerta");
            return Indisponivel("Banco de dados indisponível.");
        }
    }

    internal static async Task<IResult> ListarAsync(
        IAlertaRepository repository,
        IOptions<AlertasOptions> options,
        string? id_veiculo,
        string? tipo_infracao,
        DateTimeOffset? desde,
        DateTimeOffset? ate,
        int? limit,
        CancellationToken ct)
    {
        var (filtro, erros) = AlertaValidator.ValidarFiltro(
            id_veiculo, tipo_infracao, desde, ate, limit, options.Value);
        if (filtro is null)
            return Results.ValidationProblem(erros);

        try
        {
            return Results.Ok(await repository.ListarAsync(filtro, ct));
        }
        catch (NpgsqlException)
        {
            return Indisponivel("Banco de dados indisponível.");
        }
    }

    internal static async Task<IResult> ObterAsync(long id, IAlertaRepository repository, CancellationToken ct)
    {
        try
        {
            return await repository.ObterAsync(id, ct) is { } alerta
                ? Results.Ok(alerta)
                : Results.NotFound();
        }
        catch (NpgsqlException)
        {
            return Indisponivel("Banco de dados indisponível.");
        }
    }

    internal static async Task<IResult> ResumoAsync(
        IAlertaRepository repository,
        DateTimeOffset? desde,
        DateTimeOffset? ate,
        int? top,
        CancellationToken ct)
    {
        if (desde is not null && ate is not null && desde > ate)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["desde"] = ["desde deve ser anterior ou igual a ate."]
            });

        var topEfetivo = Math.Clamp(top ?? TopVeiculosPadrao, 1, TopVeiculosMax);

        try
        {
            return Results.Ok(await repository.ResumoAsync(desde, ate, topEfetivo, ct));
        }
        catch (NpgsqlException)
        {
            return Indisponivel("Banco de dados indisponível.");
        }
    }

    private static IResult Indisponivel(string detalhe) =>
        Results.Problem(detalhe, statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Serviço temporariamente indisponível");
}
