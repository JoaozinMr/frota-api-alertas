using System.Text;
using FrotaAlertas.Models;
using FrotaAlertas.Options;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FrotaAlertas.Services;

public interface IAlertaRepository
{
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Insere de forma idempotente. <c>Criado=false</c> indica duplicata (registro existente devolvido).</summary>
    Task<(AlertaResponse Alerta, bool Criado)> InserirAsync(NovoAlerta alerta, CancellationToken ct);

    Task<AlertaResponse?> ObterAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<AlertaResponse>> ListarAsync(AlertaFiltro filtro, CancellationToken ct);

    Task<AlertaResumo> ResumoAsync(DateTimeOffset? desde, DateTimeOffset? ate, int topVeiculos, CancellationToken ct);

    Task PingAsync(CancellationToken ct);
}

/// <summary>
/// Persistência na tabela própria <c>alertas</c> (a tabela <c>telemetria</c> pertence ao Worker).
/// Idempotência via <c>UNIQUE (id_veiculo, tipo_infracao, timestamp)</c> + <c>ON CONFLICT DO NOTHING</c>.
/// </summary>
public sealed class PostgresAlertaRepository(NpgsqlDataSource dataSource) : IAlertaRepository
{
    private const string Colunas =
        "id, id_veiculo, tipo_infracao, descricao, latitude, longitude, velocidade, \"timestamp\", correlation_id, recebido_em";

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS alertas (
            id              BIGSERIAL PRIMARY KEY,
            id_veiculo      TEXT             NOT NULL,
            tipo_infracao   TEXT             NOT NULL,
            descricao       TEXT             NOT NULL DEFAULT '',
            latitude        DOUBLE PRECISION NULL,
            longitude       DOUBLE PRECISION NULL,
            velocidade      DOUBLE PRECISION NULL,
            "timestamp"     TIMESTAMPTZ      NOT NULL,
            correlation_id  TEXT             NULL,
            recebido_em     TIMESTAMPTZ      NOT NULL DEFAULT now(),
            CONSTRAINT uq_alertas_veiculo_tipo_timestamp UNIQUE (id_veiculo, tipo_infracao, "timestamp")
        );
        CREATE INDEX IF NOT EXISTS ix_alertas_timestamp ON alertas ("timestamp" DESC);
        CREATE INDEX IF NOT EXISTS ix_alertas_veiculo_timestamp ON alertas (id_veiculo, "timestamp" DESC);
        CREATE INDEX IF NOT EXISTS ix_alertas_tipo_timestamp ON alertas (tipo_infracao, "timestamp" DESC);
        """;

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(SchemaSql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(AlertaResponse Alerta, bool Criado)> InserirAsync(NovoAlerta alerta, CancellationToken ct)
    {
        // Npgsql exige offset 0 para timestamptz.
        var timestampUtc = alerta.Timestamp.ToUniversalTime();

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO alertas (id_veiculo, tipo_infracao, descricao, latitude, longitude, velocidade, "timestamp", correlation_id)
            VALUES (@id_veiculo, @tipo_infracao, @descricao, @latitude, @longitude, @velocidade, @timestamp, @correlation_id)
            ON CONFLICT (id_veiculo, tipo_infracao, "timestamp") DO NOTHING
            RETURNING {Colunas}
            """, conn))
        {
            insert.Parameters.AddWithValue("id_veiculo", alerta.IdVeiculo);
            insert.Parameters.AddWithValue("tipo_infracao", alerta.TipoInfracao);
            insert.Parameters.AddWithValue("descricao", alerta.Descricao);
            insert.Parameters.Add(Nullable("latitude", alerta.Latitude));
            insert.Parameters.Add(Nullable("longitude", alerta.Longitude));
            insert.Parameters.Add(Nullable("velocidade", alerta.Velocidade));
            insert.Parameters.AddWithValue("timestamp", NpgsqlDbType.TimestampTz, timestampUtc);
            insert.Parameters.AddWithValue("correlation_id", alerta.CorrelationId);

            await using var reader = await insert.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return (Ler(reader), true);
        }

        // Duplicata: devolve o registro existente (a API nunca responde 409 ao Worker).
        await using var select = new NpgsqlCommand($"""
            SELECT {Colunas} FROM alertas
            WHERE id_veiculo = @id_veiculo AND tipo_infracao = @tipo_infracao AND "timestamp" = @timestamp
            """, conn);
        select.Parameters.AddWithValue("id_veiculo", alerta.IdVeiculo);
        select.Parameters.AddWithValue("tipo_infracao", alerta.TipoInfracao);
        select.Parameters.AddWithValue("timestamp", NpgsqlDbType.TimestampTz, timestampUtc);

        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                return (Ler(reader), false);
        }

        // Só acontece se o registro conflitante for removido entre os dois comandos.
        throw new InvalidOperationException("Conflito de alerta detectado, mas o registro existente não foi encontrado.");
    }

    public async Task<AlertaResponse?> ObterAsync(long id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {Colunas} FROM alertas WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Ler(reader) : null;
    }

    public async Task<IReadOnlyList<AlertaResponse>> ListarAsync(AlertaFiltro filtro, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand();
        var sql = new StringBuilder($"SELECT {Colunas} FROM alertas WHERE TRUE");

        if (filtro.IdVeiculo is not null)
        {
            sql.Append(" AND id_veiculo = @id_veiculo");
            cmd.Parameters.AddWithValue("id_veiculo", filtro.IdVeiculo);
        }
        if (filtro.TipoInfracao is not null)
        {
            sql.Append(" AND tipo_infracao = @tipo_infracao");
            cmd.Parameters.AddWithValue("tipo_infracao", filtro.TipoInfracao);
        }
        AplicarPeriodo(sql, cmd, filtro.Desde, filtro.Ate);

        sql.Append(" ORDER BY \"timestamp\" DESC, id DESC LIMIT @limit");
        cmd.Parameters.AddWithValue("limit", filtro.Limit);
        cmd.CommandText = sql.ToString();

        var resultado = new List<AlertaResponse>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            resultado.Add(Ler(reader));
        return resultado;
    }

    public async Task<AlertaResumo> ResumoAsync(DateTimeOffset? desde, DateTimeOffset? ate, int topVeiculos, CancellationToken ct)
    {
        var porTipo = TipoInfracao.Todos.ToDictionary(t => t, _ => 0L);

        await using (var cmd = dataSource.CreateCommand())
        {
            var sql = new StringBuilder("SELECT tipo_infracao, COUNT(*) FROM alertas WHERE TRUE");
            AplicarPeriodo(sql, cmd, desde, ate);
            sql.Append(" GROUP BY tipo_infracao");
            cmd.CommandText = sql.ToString();

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                porTipo[reader.GetString(0)] = reader.GetInt64(1);
        }

        var top = new List<VeiculoContagem>();
        await using (var cmd = dataSource.CreateCommand())
        {
            var sql = new StringBuilder("SELECT id_veiculo, COUNT(*), MAX(\"timestamp\") FROM alertas WHERE TRUE");
            AplicarPeriodo(sql, cmd, desde, ate);
            sql.Append(" GROUP BY id_veiculo ORDER BY COUNT(*) DESC, id_veiculo LIMIT @top");
            cmd.Parameters.AddWithValue("top", topVeiculos);
            cmd.CommandText = sql.ToString();

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                top.Add(new VeiculoContagem(
                    reader.GetString(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return new AlertaResumo(desde, ate, porTipo.Values.Sum(), porTipo, top);
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
    }

    private static void AplicarPeriodo(StringBuilder sql, NpgsqlCommand cmd, DateTimeOffset? desde, DateTimeOffset? ate)
    {
        if (desde is not null)
        {
            sql.Append(" AND \"timestamp\" >= @desde");
            cmd.Parameters.AddWithValue("desde", NpgsqlDbType.TimestampTz, desde.Value.ToUniversalTime());
        }
        if (ate is not null)
        {
            sql.Append(" AND \"timestamp\" <= @ate");
            cmd.Parameters.AddWithValue("ate", NpgsqlDbType.TimestampTz, ate.Value.ToUniversalTime());
        }
    }

    private static NpgsqlParameter Nullable(string nome, double? valor) =>
        new(nome, NpgsqlDbType.Double) { Value = valor.HasValue ? valor.Value : DBNull.Value };

    private static AlertaResponse Ler(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetDouble(4),
        r.IsDBNull(5) ? null : r.GetDouble(5),
        r.IsDBNull(6) ? null : r.GetDouble(6),
        r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.GetFieldValue<DateTimeOffset>(9));
}

/// <summary>Sinaliza se o schema já foi criado (POSTs antes disso recebem 503, transitório para o Worker).</summary>
public sealed class SchemaState
{
    private volatile bool _pronto;
    public bool Pronto => _pronto;
    public void MarcarPronto() => _pronto = true;
}

/// <summary>
/// Cria o schema na inicialização, repetindo enquanto o banco não estiver disponível
/// (mesma abordagem do <c>EnsureSchemaAsync</c> do Worker), sem depender da ordem do Compose.
/// </summary>
public sealed class SchemaInitializerService(
    IAlertaRepository repository,
    SchemaState state,
    IOptions<PostgresOptions> options,
    ILogger<SchemaInitializerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromSeconds(Math.Max(1, options.Value.SchemaRetrySeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await repository.EnsureSchemaAsync(stoppingToken);
                state.MarcarPronto();
                logger.LogInformation("Schema da tabela alertas verificado/criado");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PostgreSQL indisponível ao criar schema; nova tentativa em {Intervalo}s",
                    intervalo.TotalSeconds);
            }

            try { await Task.Delay(intervalo, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
