using System.Diagnostics;
using Gateway.Clients;
using Gateway.Data;
using Gateway.Endpoints;
using Gateway.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logs (SPEC 8.2). Trace-ID enrichment arrives with OpenTelemetry in Phase 4.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
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

// Every log line carries the service name so the two services stay distinguishable
// once their output is interleaved (SPEC 8.2).
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Request");
    using (logger.BeginScope(new Dictionary<string, object> { ["service"] = "gateway" }))
    {
        await next();
    }
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapEventEndpoints();

app.Run();
