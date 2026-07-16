using System.Diagnostics;
using Gateway.Clients;
using Gateway.Data;
using Gateway.Endpoints;
using Gateway.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logs (SPEC 8.2). Trace-ID enrichment arrives with OpenTelemetry in Phase 4.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

builder.Services.AddDbContext<GatewayDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("EventsDb") ?? "Data Source=events.db"));

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

// The Gateway's only route into the Account Service. Phase 3 layers the resilience
// pipeline (timeout, retry with backoff and jitter, circuit breaker) onto this client.
builder.Services.AddHttpClient<AccountServiceClient>(client =>
{
    var baseUrl = builder.Configuration["AccountService:BaseUrl"] ?? "http://localhost:8081";
    client.BaseAddress = new Uri(baseUrl);
});

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
