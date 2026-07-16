/// <summary>
/// Entry-point marker for WebApplicationFactory. Gateway.Tests references both service
/// assemblies and each defines its own top-level Program, so WebApplicationFactory is
/// pointed at an unambiguous marker type instead.
/// </summary>
public interface IGatewayApi;
