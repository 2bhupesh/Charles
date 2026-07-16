using System.Diagnostics;
using Gateway.Clients;
using Gateway.Data;
using Gateway.Diagnostics;
using Gateway.Endpoints;
using Gateway.Logging;
using Gateway.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "gateway";

// Structured JSON logs carrying the trace ID and service name (SPEC 8.2).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = JsonLogFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<JsonLogFormatter, JsonLogFormatterOptions>(
    options => options.ServiceName = ServiceName);

builder.Services.AddSingleton<GatewayMetrics>();

// Tracing (SPEC 8.1). ASP.NET Core starts a trace per inbound request, continuing an
// inbound W3C traceparent when the caller sent one, and .NET propagates it onward over
// HttpClient automatically - so one client request is one trace ID across both services.
//
// No exporter is required: the logs carry the IDs. An OTLP endpoint is left configurable
// so traces can be pointed at Jaeger without a code change.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(ServiceName))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddHttpClientInstrumentation();

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
        metrics.AddHttpClientInstrumentation();
        metrics.AddMeter(GatewayMetrics.MeterName);
        metrics.AddPrometheusExporter();
    });

// Pooling is disabled deliberately - see the same note in the Account Service. EF Core
// registers user-defined functions per SQLite connection and Microsoft.Data.Sqlite
// unregisters them when returning one to the pool; under concurrent requests that races
// with active statements and throws SQLite error 5, surfacing as a 500. Concurrent
// submissions of the same eventId hit exactly that path here.
var connectionString = new SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("EventsDb") ?? "Data Source=events.db")
{
    Pooling = false
}.ToString();

builder.Services.AddDbContext<GatewayDbContext>(options => options.UseSqlite(connectionString));

// SPEC 6: every ProblemDetails carries the trace ID, so a client-reported error can be
// found in the logs. Done centrally so framework-generated problems (malformed JSON,
// 404s, 500s) are covered too, and as the bare 32-hex trace-id rather than the default
// full traceparent - it is the trace-id that logs and traces correlate on.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["traceId"] =
        Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new Iso8601UtcConverter()));

builder.Services.AddSingleton<AccountServiceAvailability>();

// The Gateway's only route into the Account Service, wrapped in the resilience pipeline
// (SPEC 7): outer total timeout -> retry with exponential backoff and jitter -> circuit
// breaker -> per-attempt timeout.
//
// Retry absorbs transient blips; the circuit breaker turns sustained failure into
// immediate 503s, which keeps Gateway threads off a downstream that is already
// struggling and gives it room to recover. Retrying a POST is only safe because the
// downstream apply is idempotent by eventId - the resiliency and the idempotency are
// co-designed, not independent choices.
//
// The standard handler already retries only transient failures (5xx, 408, timeouts,
// transport errors) and never 4xx, which is what SPEC 7 asks for: a 4xx will not heal.
//
// Settings are bound from configuration so that tests can trip the breaker in
// milliseconds instead of waiting out production windows.
builder.Services
    .AddHttpClient<AccountServiceClient>(client =>
    {
        var baseUrl = builder.Configuration["AccountService:BaseUrl"] ?? "http://localhost:8081";
        client.BaseAddress = new Uri(baseUrl);
    })
    .AddStandardResilienceHandler()
    .Configure(builder.Configuration.GetSection("AccountService:Resilience"));

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
    var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapEventEndpoints();
app.MapPrometheusScrapingEndpoint();

app.Run();
