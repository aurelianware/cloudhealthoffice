using CloudHealthOffice.Infrastructure.Licensing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests;

public class LicenseCheckMiddlewareTests
{
    private static LicenseCheckMiddleware CreateMiddleware(
        RequestDelegate next,
        string environment = "Development",
        string? licenseKey = null,
        ILogger<LicenseCheckMiddleware>? logger = null)
    {
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns(environment);

        var configValues = new Dictionary<string, string?>();
        if (licenseKey is not null)
            configValues["CloudHealthOffice:LicenseKey"] = licenseKey;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        // Temporarily clear the env-var so tests are deterministic regardless of CI environment.
        // The middleware prefers CHO_LICENSE_KEY over IConfiguration, so a set env var would
        // cause "without license key" test cases to behave as licensed and fail.
        var savedEnvVar = Environment.GetEnvironmentVariable("CHO_LICENSE_KEY");
        Environment.SetEnvironmentVariable("CHO_LICENSE_KEY", licenseKey);
        try
        {
            return new LicenseCheckMiddleware(
                next,
                logger ?? NullLogger<LicenseCheckMiddleware>.Instance,
                env.Object,
                config);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHO_LICENSE_KEY", savedEnvVar);
        }
    }

    [Fact]
    public async Task InvokeAsync_Development_PassesThroughWithoutHeader()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, environment: "Development");

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.Headers.ContainsKey("X-CHO-License").Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_Production_WithLicenseKey_PassesThroughWithoutHeader()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, environment: "Production", licenseKey: "valid-key-123");

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.Headers.ContainsKey("X-CHO-License").Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_Production_WithoutLicenseKey_AddsUnlicensedHeader()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask, environment: "Production");

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.Headers["X-CHO-License"].ToString().Should().Be("unlicensed");
    }

    [Fact]
    public async Task InvokeAsync_Staging_WithoutLicenseKey_AddsUnlicensedHeader()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask, environment: "Staging");

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.Headers["X-CHO-License"].ToString().Should().Be("unlicensed");
    }

    [Fact]
    public async Task InvokeAsync_Production_WithoutLicenseKey_StillCallsNext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, environment: "Production");

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue("services should always function regardless of license status");
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/swagger")]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/favicon")]
    [InlineData("/favicon.ico")]
    public async Task InvokeAsync_ExemptPaths_SkipLicenseCheck(string path)
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask, environment: "Production");

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        context.Response.Headers.ContainsKey("X-CHO-License").Should().BeFalse(
            $"exempt path '{path}' should not have license header");
    }

    [Theory]
    [InlineData("/api/claims")]
    [InlineData("/api/members")]
    [InlineData("/")]
    public async Task InvokeAsync_NonExemptPaths_Production_WithoutKey_AddsHeader(string path)
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask, environment: "Production");

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        context.Response.Headers["X-CHO-License"].ToString().Should().Be("unlicensed");
    }

    [Fact]
    public async Task InvokeAsync_Production_WithoutKey_LogsWarningPeriodically()
    {
        var mockLogger = new Mock<ILogger<LicenseCheckMiddleware>>();
        var middleware = CreateMiddleware(
            _ => Task.CompletedTask,
            environment: "Production",
            logger: mockLogger.Object);

        var context = new DefaultHttpContext();

        // First request should log
        await middleware.InvokeAsync(context);

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("LICENSING")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_Production_WithoutKey_SecondRequestWithinInterval_DoesNotLogAgain()
    {
        var mockLogger = new Mock<ILogger<LicenseCheckMiddleware>>();
        var middleware = CreateMiddleware(
            _ => Task.CompletedTask,
            environment: "Production",
            logger: mockLogger.Object);

        // Two requests in quick succession
        await middleware.InvokeAsync(new DefaultHttpContext());
        await middleware.InvokeAsync(new DefaultHttpContext());

        // The per-request "LICENSING" warning should only fire once (throttled)
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("LICENSING")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Constructor_Production_WithoutKey_LogsStartupWarning()
    {
        var mockLogger = new Mock<ILogger<LicenseCheckMiddleware>>();

        CreateMiddleware(_ => Task.CompletedTask, environment: "Production", logger: mockLogger.Object);

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("commercial license")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Constructor_Production_WithKey_LogsInfoMessage()
    {
        var mockLogger = new Mock<ILogger<LicenseCheckMiddleware>>();

        CreateMiddleware(_ => Task.CompletedTask, environment: "Production", licenseKey: "key-123", logger: mockLogger.Object);

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("license key present")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Constructor_Development_WithoutKey_DoesNotLogWarning()
    {
        var mockLogger = new Mock<ILogger<LicenseCheckMiddleware>>();

        CreateMiddleware(_ => Task.CompletedTask, environment: "Development", logger: mockLogger.Object);

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }
}
