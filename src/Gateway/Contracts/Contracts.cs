using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gateway.Contracts;

/// <summary>
/// Body of POST /events (SPEC 3.1). Every field is nullable and the timestamp arrives as
/// a string so that validation - not the JSON deserializer - decides what a bad request
/// looks like, which lets us return per-field errors (SPEC 6).
/// </summary>
public sealed record SubmitEventRequest(
    string? EventId,
    string? AccountId,
    string? Type,
    decimal? Amount,
    string? Currency,
    string? EventTimestamp,
    JsonElement? Metadata);

/// <summary>The event resource (SPEC 3.5).</summary>
public sealed record EventResponse(
    string EventId,
    string AccountId,
    string Type,
    decimal Amount,
    string Currency,
    DateTimeOffset EventTimestamp,
    JsonNode? Metadata,
    string Status,
    DateTimeOffset ReceivedAt);

public sealed record EventListResponse(
    string AccountId,
    IReadOnlyList<EventResponse> Events);

public sealed record HealthResponse(
    string Status,
    string Service,
    IReadOnlyDictionary<string, string> Checks);
