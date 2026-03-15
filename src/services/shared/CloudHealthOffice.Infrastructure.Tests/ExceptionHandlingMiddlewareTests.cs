using System.Text.Json;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests;

public class ExceptionHandlingMiddlewareTests
{
    private static ExceptionHandlingMiddleware CreateMiddleware(
        RequestDelegate next,
        bool isDevelopment = false)
    {
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns(isDevelopment ? "Development" : "Production");
        return new ExceptionHandlingMiddleware(
            next,
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            env.Object);
    }

    private static async Task<StandardErrorResponse?> GetErrorResponse(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return JsonSerializer.Deserialize<StandardErrorResponse>(body,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    [Fact]
    public async Task InvokeAsync_NoException_PassesThrough()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ArgumentException_Returns400()
    {
        var middleware = CreateMiddleware(_ => throw new ArgumentException("Bad input"));
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(400);
        var error = await GetErrorResponse(context);
        error!.Code.Should().Be("BAD_REQUEST");
    }

    [Fact]
    public async Task InvokeAsync_KeyNotFoundException_Returns404()
    {
        var middleware = CreateMiddleware(_ => throw new KeyNotFoundException("Not found"));
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(404);
        var error = await GetErrorResponse(context);
        error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task InvokeAsync_UnauthorizedAccessException_Returns403()
    {
        var middleware = CreateMiddleware(_ => throw new UnauthorizedAccessException());
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(403);
        var error = await GetErrorResponse(context);
        error!.Code.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task InvokeAsync_TenantContextMissingException_Returns401()
    {
        var middleware = CreateMiddleware(_ => throw new TenantContextMissingException("No tenant"));
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(401);
        var error = await GetErrorResponse(context);
        error!.Code.Should().Be("TENANT_CONTEXT_MISSING");
    }

    [Fact]
    public async Task InvokeAsync_InvalidOperationException_Returns500()
    {
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("Something went wrong"));
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(500);
        var error = await GetErrorResponse(context);
        error!.Code.Should().Be("INVALID_OPERATION");
    }

    [Fact]
    public async Task InvokeAsync_UnknownException_Returns500()
    {
        var middleware = CreateMiddleware(_ => throw new Exception("Unexpected"));
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(500);
        var error = await GetErrorResponse(context);
        error!.Code.Should().Be("INTERNAL_ERROR");
    }

    [Fact]
    public async Task InvokeAsync_Development_IncludesExceptionDetails()
    {
        var middleware = CreateMiddleware(_ => throw new Exception("Sensitive info"), isDevelopment: true);
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(context);

        var error = await GetErrorResponse(context);
        error!.Message.Should().Be("Sensitive info");
        error.Details.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task InvokeAsync_Production_HidesExceptionDetails()
    {
        var middleware = CreateMiddleware(_ => throw new Exception("Sensitive info"), isDevelopment: false);
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(context);

        var error = await GetErrorResponse(context);
        error!.Message.Should().Be("An unexpected error occurred.");
        error.Details.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_SetsJsonContentType()
    {
        var middleware = CreateMiddleware(_ => throw new Exception("Error"));
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(context);

        context.Response.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task InvokeAsync_IncludesTraceId()
    {
        var middleware = CreateMiddleware(_ => throw new Exception("Error"));
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        await middleware.InvokeAsync(context);

        var error = await GetErrorResponse(context);
        error!.TraceId.Should().NotBeNullOrEmpty();
    }
}
