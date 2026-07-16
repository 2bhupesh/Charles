/// <summary>
/// Entry-point marker for WebApplicationFactory. Gateway.Tests hosts this service
/// alongside the Gateway for the end-to-end integration test, and each assembly defines
/// its own top-level Program, so WebApplicationFactory is pointed at an unambiguous
/// marker type instead.
/// </summary>
public interface IAccountServiceApi;
