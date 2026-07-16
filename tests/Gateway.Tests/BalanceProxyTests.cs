using System.Net;
using System.Net.Http.Json;
using AccountService.Contracts;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// SPEC 3.6 / Requirement 6 - balance queries. The Account Service is internal, so the
/// Gateway is a client's only route to a balance; when it is unreachable there is no local
/// data to fall back on and the error has to say so plainly.
/// </summary>
public sealed class BalanceProxyTests : IDisposable
{
    private readonly AccountServiceHost _accountService = new();
    private readonly GatewayFactory _gatewayFactory;
    private readonly HttpClient _gateway;

    public BalanceProxyTests()
    {
        _accountService.CreateClient();

        _gatewayFactory = new GatewayFactory(
            _accountService.Server.BaseAddress.ToString(),
            accountServiceHandler: _accountService.Server.CreateHandler());

        _gateway = _gatewayFactory.CreateClient();
    }

    public void Dispose()
    {
        _gatewayFactory.Dispose();
        _accountService.Dispose();
    }

    [Fact]
    public async Task GetBalance_AccountHasEvents_ProxiesTheAccountServicesBalance()
    {
        await _gateway.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);
        await _gateway.SubmitEventAsync("evt-2", "acct-1", "DEBIT", 24.50m);

        var balance = await _gateway.GetFromJsonAsync<BalanceResponse>("/accounts/acct-1/balance");

        Assert.Equal(125.50m, balance!.Balance);
        Assert.Equal("acct-1", balance.AccountId);
        Assert.Equal("USD", balance.Currency);
    }

    [Fact]
    public async Task GetBalance_UnknownAccount_Returns404()
    {
        var response = await _gateway.GetAsync("/accounts/nobody/balance");

        // Not a downstream failure - the account genuinely does not exist.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>The balance proxy when the Account Service cannot be reached at all.</summary>
public sealed class BalanceProxyDegradationTests : IDisposable
{
    private readonly FakeAccountService _accountService = new();
    private readonly GatewayFactory _factory;
    private readonly HttpClient _gateway;

    public BalanceProxyDegradationTests()
    {
        // One retry with no delay: the pipeline is still real, the test is still quick.
        _factory = new GatewayFactory(_accountService.Url, settings: new Dictionary<string, string>
        {
            ["AccountService:Resilience:Retry:MaxRetryAttempts"] = "1",
            ["AccountService:Resilience:Retry:Delay"] = "00:00:00"
        });

        _gateway = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        _accountService.Dispose();
    }

    [Fact]
    public async Task GetBalance_AccountServiceDown_Returns503NamingTheAccountService()
    {
        _accountService.Stop();

        var response = await _gateway.GetAsync("/accounts/acct-1/balance");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        // The client must be able to tell "the balance service is down" from "your account
        // does not exist" - a 404 here would be a lie (Requirement 6).
        Assert.Contains("Account Service", problem!.Detail);
        Assert.Contains("unreachable", problem.Detail);
    }

    [Fact]
    public async Task GetBalance_AccountServiceDown_DoesNotBreakTheEventEndpoints()
    {
        await _gateway.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        _accountService.Stop();

        var balance = await _gateway.GetAsync("/accounts/acct-1/balance");
        var events = await _gateway.GetAsync("/events?account=acct-1");
        var single = await _gateway.GetAsync("/events/evt-1");

        // The degradation matrix in SPEC 7.1: only the balance depends on the downstream.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, balance.StatusCode);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
    }
}
