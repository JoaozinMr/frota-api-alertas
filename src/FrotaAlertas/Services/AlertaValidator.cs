using FrotaAlertas.Models;
using FrotaAlertas.Options;

namespace FrotaAlertas.Services;

/// <summary>
/// Regras puras de validação. Só rejeitamos (400) payloads realmente inválidos: qualquer 4xx
/// faz o Worker desviar a telemetria original para a DLQ.
/// </summary>
public static class AlertaValidator
{
    public const int IdVeiculoMaxLength = 100;
    public const int DescricaoMaxLength = 2000;

    public sealed record Resultado(NovoAlerta? Alerta, IDictionary<string, string[]> Erros)
    {
        public bool Valido => Erros.Count == 0;
    }

    public static Resultado Validar(AlertaRequest? request, string correlationId)
    {
        var erros = new Dictionary<string, string[]>();

        if (request is null)
        {
            erros["body"] = ["Corpo da requisição ausente ou inválido."];
            return new Resultado(null, erros);
        }

        var idVeiculo = request.IdVeiculo?.Trim();
        if (string.IsNullOrEmpty(idVeiculo))
            erros["id_veiculo"] = ["id_veiculo é obrigatório."];
        else if (idVeiculo.Length > IdVeiculoMaxLength)
            erros["id_veiculo"] = [$"id_veiculo deve ter no máximo {IdVeiculoMaxLength} caracteres."];

        if (!TipoInfracao.EhValido(request.TipoInfracao))
            erros["tipo_infracao"] =
                [$"tipo_infracao deve ser um de: {string.Join(", ", TipoInfracao.Todos)}."];

        if (request.Timestamp is null)
            erros["timestamp"] = ["timestamp é obrigatório (ISO-8601 com offset)."];

        if (request.Latitude is { } lat && (lat < -90 || lat > 90))
            erros["latitude"] = ["latitude deve estar entre -90 e 90."];

        if (request.Longitude is { } lon && (lon < -180 || lon > 180))
            erros["longitude"] = ["longitude deve estar entre -180 e 180."];

        if (request.Velocidade is < 0)
            erros["velocidade"] = ["velocidade não pode ser negativa."];

        // descricao é texto livre gerado pelo Worker: ausência não invalida o alerta,
        // apenas truncamos se vier gigante.
        var descricao = request.Descricao ?? string.Empty;
        if (descricao.Length > DescricaoMaxLength)
            descricao = descricao[..DescricaoMaxLength];

        if (erros.Count > 0)
            return new Resultado(null, erros);

        var alerta = new NovoAlerta(
            idVeiculo!,
            request.TipoInfracao!,
            descricao,
            request.Latitude,
            request.Longitude,
            request.Velocidade,
            request.Timestamp!.Value,
            correlationId);

        return new Resultado(alerta, erros);
    }

    /// <summary>Valida e normaliza os filtros de <c>GET /api/alerts</c>.</summary>
    public static (AlertaFiltro? Filtro, IDictionary<string, string[]> Erros) ValidarFiltro(
        string? idVeiculo, string? tipoInfracao, DateTimeOffset? desde, DateTimeOffset? ate,
        int? limit, AlertasOptions options)
    {
        var erros = new Dictionary<string, string[]>();

        var tipo = string.IsNullOrWhiteSpace(tipoInfracao) ? null : tipoInfracao.Trim();
        if (tipo is not null && !TipoInfracao.EhValido(tipo))
            erros["tipo_infracao"] =
                [$"tipo_infracao deve ser um de: {string.Join(", ", TipoInfracao.Todos)}."];

        if (desde is not null && ate is not null && desde > ate)
            erros["desde"] = ["desde deve ser anterior ou igual a ate."];

        if (limit is <= 0)
            erros["limit"] = ["limit deve ser maior que zero."];

        if (erros.Count > 0)
            return (null, erros);

        var efetivo = Math.Min(limit ?? options.DefaultLimit, options.MaxLimit);
        var veiculo = string.IsNullOrWhiteSpace(idVeiculo) ? null : idVeiculo.Trim();

        return (new AlertaFiltro(veiculo, tipo, desde, ate, efetivo), erros);
    }
}
