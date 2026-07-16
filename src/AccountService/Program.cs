using System.Diagnostics;
using AccountService.Data;
using AccountService.Endpoints;
using AccountService.Serialization;
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

builder.Services.AddDbContext<AccountDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("AccountsDb") ?? "Data Source=accounts.db"));

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

// Every log line carries the service name so the two services stay distinguishable
// once their output is interleaved (SPEC 8.2).
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Request");
    using (logger.BeginScope(new Dictionary<string, object> { ["service"] = "account-service" }))
    {
        await next();
    }
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AccountDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapAccountEndpoints();

app.Run();

// Exposed so WebApplicationFactory<Program> can host this app in tests.
public partial class Program;
