using System.Text.Json.Serialization;

namespace FrotaAlertas.Models;

/// <summary>Tipos de infração aceitos (enum fechado, mesmo conjunto emitido pelo Worker).</summary>
public static class TipoInfracao
{
    public const string ExcessoVelocidade = "EXCESSO_VELOCIDADE";
    public const string ForaGeofence = "FORA_GEOFENCE";
    public const string SensorMorto = "SENSOR_MORTO";

    public static readonly IReadOnlySet<string> Todos =
        new HashSet<string>(StringComparer.Ordinal) { ExcessoVelocidade, ForaGeofence, SensorMorto };

    public static bool EhValido(string? tipo) => tipo is not null && Todos.Contains(tipo);
}

/// <summary>
/// Payload recebido em <c>POST /api/alerts</c>. Espelha o <c>AlertaPayload</c> do Worker.
/// Campos de valor são nullable para que a validação devolva 400 com mensagem clara em vez de
/// assumir valores padrão silenciosamente.
/// </summary>
public sealed record AlertaRequest(
    [property: JsonPropertyName("id_veiculo")] string? IdVeiculo,
    [property: JsonPropertyName("tipo_infracao")] string? TipoInfracao,
    [property: JsonPropertyName("descricao")] string? Descricao,
    [property: JsonPropertyName("latitude")] double? Latitude,
    [property: JsonPropertyName("longitude")] double? Longitude,
    [property: JsonPropertyName("velocidade")] double? Velocidade,
    [property: JsonPropertyName("timestamp")] DateTimeOffset? Timestamp,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId);

/// <summary>Alerta já validado, pronto para persistir.</summary>
public sealed record NovoAlerta(
    string IdVeiculo,
    string TipoInfracao,
    string Descricao,
    double? Latitude,
    double? Longitude,
    double? Velocidade,
    DateTimeOffset Timestamp,
    string CorrelationId);

/// <summary>Alerta persistido, devolvido pelos endpoints.</summary>
public sealed record AlertaResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("id_veiculo")] string IdVeiculo,
    [property: JsonPropertyName("tipo_infracao")] string TipoInfracao,
    [property: JsonPropertyName("descricao")] string Descricao,
    [property: JsonPropertyName("latitude")] double? Latitude,
    [property: JsonPropertyName("longitude")] double? Longitude,
    [property: JsonPropertyName("velocidade")] double? Velocidade,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId,
    [property: JsonPropertyName("recebido_em")] DateTimeOffset RecebidoEm);

/// <summary>Filtros de <c>GET /api/alerts</c>, já normalizados.</summary>
public sealed record AlertaFiltro(
    string? IdVeiculo,
    string? TipoInfracao,
    DateTimeOffset? Desde,
    DateTimeOffset? Ate,
    int Limit);

/// <summary>Resumo agregado para painéis (Grafana / dashboard web).</summary>
public sealed record AlertaResumo(
    [property: JsonPropertyName("desde")] DateTimeOffset? Desde,
    [property: JsonPropertyName("ate")] DateTimeOffset? Ate,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("por_tipo")] IReadOnlyDictionary<string, long> PorTipo,
    [property: JsonPropertyName("top_veiculos")] IReadOnlyList<VeiculoContagem> TopVeiculos);

public sealed record VeiculoContagem(
    [property: JsonPropertyName("id_veiculo")] string IdVeiculo,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("ultimo_alerta")] DateTimeOffset UltimoAlerta);
