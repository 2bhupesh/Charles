namespace AccountService.Http;

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
}
