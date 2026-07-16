using System.Diagnostics;
using AccountService.Data;
using AccountService.Diagnostics;
using AccountService.Endpoints;
using AccountService.Logging;
using AccountService.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "account-service";

// Structured JSON logs carrying the trace ID and service name (SPEC 8.2).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = JsonLogFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<JsonLogFormatter, JsonLogFormatterOptions>(
    options => options.ServiceName = ServiceName);

builder.Services.AddSingleton<AccountMetrics>();

// Tracing (SPEC 8.1). ASP.NET Core continues the W3C traceparent the Gateway sends, which
// is what makes a single client request traceable across both services. No outbound
// instrumentation: this service calls nobody.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(ServiceName))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();

        var otlpEndpoint = builder.Configuration["Otlp:Endpoint"];

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint));
    })
    // Metrics on GET /metrics in Prometheus format (SPEC 8.3). ASP.NET Core instrumentation
    // supplies http_server_request_duration_seconds - request count, latency and error rate
    // are all derivable from one histogram labelled by route and status.
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddMeter(AccountMetrics.MeterName);
        metrics.AddPrometheusExporter();
    });

// Pooling is disabled deliberately. EF Core registers user-defined functions on each
// SQLite connection, and Microsoft.Data.Sqlite unregisters them when a connection is
// returned to the pool. Under concurrent requests that cleanup can race with statements
// still active on the connection and throw SQLite error 5 ("unable to delete/modify
// user-function due to active statements"), surfacing as a 500 on an otherwise valid
// request. Opening a connection to a local file is cheap, so the pool buys little here
// and costs correctness under load.
var connectionString = new SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("AccountsDb") ?? "Data Source=accounts.db")
{
    Pooling = false
}.ToString();

builder.Services.AddDbContext<AccountDbContext>(options => options.UseSqlite(connectionString));

// SPEC 6: every ProblemDetails carries the trace ID, so a client-reported error can be
// found in the logs. Done centrally so framework-generated problems (malformed JSON,
// 404s, 500s) are covered too, and as the bare 32-hex trace-id rather than the default
// full traceparent - it is the trace-id that logs and traces correlate on.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["traceId"] =
        Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new Iso8601UtcConverter()));

var app = builder.Build();

// Minimal APIs surface an unparseable body as BadHttpRequestException in Development
// (RequestDelegateFactory.ThrowOnBadRequest) but return 400 directly elsewhere. Without
// this selector the exception handler would turn malformed JSON into a 500 in
// Development only - mapping it explicitly keeps bad input a 400 in every environment.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception is BadHttpRequestException
        ? StatusCodes.Status400BadRequest
        : StatusCodes.Status500InternalServerError
});
app.UseStatusCodePages();

// Request completion (SPEC 8.2). The formatter stamps the service name and trace ID onto
// every line, so no scope is needed for those.
var requestLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Request");

app.Use(async (context, next) =>
{
    var start = Stopwatch.GetTimestamp();

    try
    {
        await next();
    }
    finally
    {
        requestLogger.LogInformation("{Method} {Path} responded {StatusCode} in {DurationMs:F1}ms",
            context.Request.Method,
            context.Request.Path.Value,
            context.Response.StatusCode,
            Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AccountDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapAccountEndpoints();
app.MapPrometheusScrapingEndpoint();

app.Run();

// Exposed so WebApplicationFactory<Program> can host this app in tests.
public partial class Program;
