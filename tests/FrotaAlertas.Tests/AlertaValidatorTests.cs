using System.Text.Json;
using FrotaAlertas.Models;
using FrotaAlertas.Options;
using FrotaAlertas.Services;

namespace FrotaAlertas.Tests;

public sealed class AlertaValidatorTests
{
    private static readonly DateTimeOffset Ts = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static AlertaRequest Valido(
        string? id = "V-001", string? tipo = TipoInfracao.ExcessoVelocidade,
        double? lat = -23.55, double? lon = -46.63, double? vel = 92.3,
        DateTimeOffset? ts = null, string? descricao = "Velocidade alta") =>
        new(id, tipo, descricao, lat, lon, vel, ts ?? Ts, "corr-1");

    [Fact]
    public void Payload_valido_gera_alerta()
    {
        var r = AlertaValidator.Validar(Valido(), "corr-1");

        Assert.True(r.Valido);
        Assert.Equal("V-001", r.Alerta!.IdVeiculo);
        Assert.Equal("corr-1", r.Alerta.CorrelationId);
    }

    [Fact]
    public void Campos_numericos_nulos_sao_aceitos()
    {
        // SENSOR_MORTO pode vir sem posição/velocidade.
        var r = AlertaValidator.Validar(
            Valido(tipo: TipoInfracao.SensorMorto, lat: null, lon: null, vel: null), "c");

        Assert.True(r.Valido);
        Assert.Null(r.Alerta!.Latitude);
        Assert.Null(r.Alerta.Velocidade);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Id_veiculo_vazio_e_invalido(string? id)
    {
        var r = AlertaValidator.Validar(Valido(id: id), "c");
        Assert.Contains("id_veiculo", r.Erros.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("excesso_velocidade")]
    [InlineData("OUTRO")]
    public void Tipo_fora_do_enum_e_invalido(string? tipo)
    {
        var r = AlertaValidator.Validar(Valido(tipo: tipo), "c");
        Assert.Contains("tipo_infracao", r.Erros.Keys);
    }

    [Fact]
    public void Timestamp_ausente_e_invalido()
    {
        var req = Valido() with { Timestamp = null };
        Assert.Contains("timestamp", AlertaValidator.Validar(req, "c").Erros.Keys);
    }

    [Fact]
    public void Coordenadas_fora_do_intervalo_sao_invalidas()
    {
        var r = AlertaValidator.Validar(Valido(lat: 91, lon: -181, vel: -1), "c");

        Assert.Contains("latitude", r.Erros.Keys);
        Assert.Contains("longitude", r.Erros.Keys);
        Assert.Contains("velocidade", r.Erros.Keys);
    }

    [Fact]
    public void Descricao_ausente_vira_vazia_e_longa_e_truncada()
    {
        Assert.Equal(string.Empty, AlertaValidator.Validar(Valido(descricao: null), "c").Alerta!.Descricao);

        var longa = new string('x', AlertaValidator.DescricaoMaxLength + 50);
        Assert.Equal(AlertaValidator.DescricaoMaxLength,
            AlertaValidator.Validar(Valido(descricao: longa), "c").Alerta!.Descricao.Length);
    }

    [Fact]
    public void Body_nulo_e_invalido()
    {
        Assert.False(AlertaValidator.Validar(null, "c").Valido);
    }

    [Fact]
    public void Desserializa_payload_snake_case_do_worker()
    {
        const string json = """
            {"id_veiculo":"V-001","tipo_infracao":"FORA_GEOFENCE","descricao":"Fora",
             "latitude":null,"longitude":null,"velocidade":null,
             "timestamp":"2026-10-10T09:00:00-03:00","correlation_id":"abc"}
            """;

        var req = JsonSerializer.Deserialize<AlertaRequest>(json)!;

        Assert.Equal("V-001", req.IdVeiculo);
        Assert.Equal(TipoInfracao.ForaGeofence, req.TipoInfracao);
        Assert.Null(req.Latitude);
        Assert.Equal(Ts, req.Timestamp); // mesmo instante, offset diferente
        Assert.Equal("abc", req.CorrelationId);
    }

    [Fact]
    public void Filtro_aplica_limite_padrao_e_maximo()
    {
        var opt = new AlertasOptions { DefaultLimit = 100, MaxLimit = 1000 };

        Assert.Equal(100, AlertaValidator.ValidarFiltro(null, null, null, null, null, opt).Filtro!.Limit);
        Assert.Equal(1000, AlertaValidator.ValidarFiltro(null, null, null, null, 5000, opt).Filtro!.Limit);
    }

    [Fact]
    public void Filtro_invalido_retorna_erros()
    {
        var opt = new AlertasOptions();
        var (filtro, erros) = AlertaValidator.ValidarFiltro(
            " ", "XYZ", Ts.AddHours(1), Ts, 0, opt);

        Assert.Null(filtro);
        Assert.Contains("tipo_infracao", erros.Keys);
        Assert.Contains("desde", erros.Keys);
        Assert.Contains("limit", erros.Keys);
    }

    [Fact]
    public void Filtro_normaliza_strings_vazias_para_null()
    {
        var f = AlertaValidator.ValidarFiltro("  V-9 ", "", null, null, 10, new AlertasOptions()).Filtro!;

        Assert.Equal("V-9", f.IdVeiculo);
        Assert.Null(f.TipoInfracao);
    }
}
