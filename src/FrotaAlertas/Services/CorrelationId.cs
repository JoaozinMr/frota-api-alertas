using System.Text.Json;
using Serilog.Context;

namespace FrotaAlertas.Services;

/// <summary>Regra de resolução do Correlation ID: header → body → GUID novo.</summary>
public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";
    public const string LogProperty = "CorrelationId";
    public const int MaxLength = 128;

    public static string Resolver(string? header, string? body)
    {
        if (Normalizar(header) is { } h) return h;
        if (Normalizar(body) is { } b) return b;
        return Guid.NewGuid().ToString();
    }

    public static string Obter(HttpContext context) =>
        context.Items[ItemKey] as string ?? Resolver(null, null);

    private static string? Normalizar(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var v = valor.Trim();
        return v.Length > MaxLength ? v[..MaxLength] : v;
    }
}

/// <summary>
/// Resolve o Correlation ID, coloca no <see cref="LogContext"/>, em <c>HttpContext.Items</c>
/// e no header da resposta. Deve ser registrado antes do request logging do Serilog.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    private const long MaxBodyPeek = 64 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        string? header = context.Request.Headers[CorrelationId.HeaderName];
        string? body = null;

        if (string.IsNullOrWhiteSpace(header))
            body = await LerCorrelationIdDoBodyAsync(context.Request);

        var correlationId = CorrelationId.Resolver(header, body);
        context.Items[CorrelationId.ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationId.HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty(CorrelationId.LogProperty, correlationId))
        {
            await next(context);
        }
    }

    /// <summary>
    /// Fallback sem header: espia o campo <c>correlation_id</c> do JSON, reposicionando o
    /// stream para que o model binding leia o corpo normalmente depois.
    /// </summary>
    private static async Task<string?> LerCorrelationIdDoBodyAsync(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method) ||
            request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true ||
            request.ContentLength is > MaxBodyPeek)
            return null;

        request.EnableBuffering();
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("correlation_id", out var prop) &&
                   prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null; // o model binding vai devolver 400 com o erro de JSON
        }
        finally
        {
            request.Body.Position = 0;
        }
    }
}
