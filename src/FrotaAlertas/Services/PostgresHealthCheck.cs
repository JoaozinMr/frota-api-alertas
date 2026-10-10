using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FrotaAlertas.Services;

/// <summary>Saudável somente se o schema já foi criado e o PostgreSQL responde.</summary>
public sealed class PostgresHealthCheck(IAlertaRepository repository, SchemaState state) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!state.Pronto)
            return HealthCheckResult.Unhealthy("Schema da tabela alertas ainda não foi criado.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        try
        {
            await repository.PingAsync(cts.Token);
            return HealthCheckResult.Healthy("PostgreSQL acessível.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL inacessível.", ex);
        }
    }
}
