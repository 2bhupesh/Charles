using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AccountService.Tests;

/// <summary>SPEC 10.1 - A5. Accounts are implicit: unknown until they have a transaction.</summary>
public sealed class AccountQueryTests : IDisposable
{
    private readonly AccountServiceFactory _factory = new();
    private readonly HttpClient _client;

    public AccountQueryTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/accounts/nobody/balance")]
    [InlineData("/accounts/nobody")]
    public async Task Get_UnknownAccount_Returns404ProblemDetails(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(StatusCodes.Status404NotFound, problem!.Status);
        Assert.Contains("nobody", problem.Detail);
    }
}
