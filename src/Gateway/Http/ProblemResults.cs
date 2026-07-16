namespace Gateway.Http;

/// <summary>
/// RFC 7807 responses (SPEC 6). The traceId extension is attached centrally by
/// CustomizeProblemDetails in Program.cs, so it is not repeated here.
/// </summary>
public static class ProblemResults
{
    public static IResult Validation(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors,
            detail: "One or more fields are invalid.",
            title: "Validation failed");

    public static IResult NotFound(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    /// <summary>
    /// The downstream is unreachable. The wording tells the client the event survived and
    /// that resubmitting the same eventId is safe - which it is, because both services
    /// deduplicate on it (SPEC 6).
    /// </summary>
    public static IResult AccountServiceUnavailable(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "5";

        return Results.Problem(
            detail: "Account Service is currently unavailable. The event was recorded and may be retried with the same eventId.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service unavailable");
    }

    /// <summary>
    /// A balance cannot be served from local data - only the Account Service knows it - so
    /// the error says plainly which dependency is down rather than implying the account or
    /// the Gateway is at fault (SPEC 6).
    /// </summary>
    public static IResult BalanceUnavailable(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "5";

        return Results.Problem(
            detail: "Account Service is unreachable, so the balance cannot be retrieved. The Gateway's event endpoints are unaffected.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service unavailable");
    }

    public static IResult BadGateway(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status502BadGateway,
            title: "Bad gateway");
}
