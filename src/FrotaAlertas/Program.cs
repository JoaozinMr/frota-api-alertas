using FrotaAlertas.Options;
using FrotaAlertas.Services;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "frota-api-alertas")
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "frota-api-alertas")
        .WriteTo.Console(new RenderedCompactJsonFormatter()));

    builder.Services.Configure<PostgresOptions>(builder.Configuration.GetSection(PostgresOptions.Section));
    builder.Services.Configure<AlertasOptions>(builder.Configuration.GetSection(AlertasOptions.Section));

    builder.Services.AddSingleton(sp =>
        NpgsqlDataSource.Create(sp.GetRequiredService<IOptions<PostgresOptions>>().Value.ConnectionString));
    builder.Services.AddSingleton<IAlertaRepository, PostgresAlertaRepository>();
    builder.Services.AddSingleton<SchemaState>();
    builder.Services.AddHostedService<SchemaInitializerService>();

    builder.Services.AddProblemDetails();
    builder.Services.AddHealthChecks()
        .AddCheck<PostgresHealthCheck>("postgres");

    var app = builder.Build();

    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging(o =>
    {
        o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} respondeu {StatusCode} em {Elapsed:0.0} ms";
        // /health é chamado a cada poucos segundos pelo Docker: rebaixa para Debug.
        o.GetLevel = (ctx, _, ex) =>
            ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
            : ctx.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Debug
            : Serilog.Events.LogEventLevel.Information;
    });
    app.UseExceptionHandler();

    app.MapHealthChecks("/health");
    AlertaEndpoints.Map(app);

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "API de Alertas encerrada inesperadamente");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposto para testes de integração (WebApplicationFactory).</summary>
public partial class Program;
