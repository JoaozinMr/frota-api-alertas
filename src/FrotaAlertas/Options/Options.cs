namespace FrotaAlertas.Options;

public sealed class PostgresOptions
{
    public const string Section = "Postgres";

    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=frota;Username=frota;Password=frota";

    /// <summary>Intervalo entre tentativas de criar o schema enquanto o banco não sobe.</summary>
    public int SchemaRetrySeconds { get; set; } = 3;
}

public sealed class AlertasOptions
{
    public const string Section = "Alertas";

    /// <summary>
    /// Orçamento máximo do POST. Precisa ficar abaixo do timeout de 3s do Polly no Worker;
    /// se estourar, a API responde 503 (transitório) em vez de deixar o Worker esperar.
    /// </summary>
    public int RequestTimeoutMs { get; set; } = 2000;

    public int DefaultLimit { get; set; } = 100;

    public int MaxLimit { get; set; } = 1000;
}
