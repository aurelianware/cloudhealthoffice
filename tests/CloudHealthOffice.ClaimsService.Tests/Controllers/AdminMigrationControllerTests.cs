using ClaimsService.Controllers;
using ClaimsService.Models.Migrations;
using ClaimsService.Services.Migrations;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// Capability 5.1b — admin endpoint contract tests. Mirrors the
/// shape established by NetworkTierBackfillAdminControllerTests:
/// feature-flag tripwire, request validation, forwarding to the
/// underlying migration service, status retrieval, and concurrent-run
/// 409 mapping.
/// </summary>
public sealed class AdminMigrationControllerTests
{
    [Fact]
    public async Task Run_Returns_503_When_Feature_Flag_Is_Off()
    {
        var (controller, _) = Build(enabled: false);

        var response = await controller.Run(new ClaimMigrationRequest(), default);

        var status = response.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Run_Returns_400_When_BatchSize_Is_Zero()
    {
        var (controller, _) = Build(enabled: true);

        var response = await controller.Run(new ClaimMigrationRequest { BatchSize = 0 }, default);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Run_Returns_400_When_BatchSize_Is_Negative()
    {
        var (controller, _) = Build(enabled: true);

        var response = await controller.Run(new ClaimMigrationRequest { BatchSize = -1 }, default);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Run_With_Null_Body_Defaults_To_DryRun_True()
    {
        var (controller, service) = Build(enabled: true);
        service.NextResult = new ClaimMigrationResult { DryRun = true, Outcome = "success" };

        var response = await controller.Run(null, default);

        response.Result.Should().BeOfType<OkObjectResult>();
        service.LastRequest.Should().NotBeNull();
        service.LastRequest!.DryRun.Should().BeTrue();
    }

    [Fact]
    public async Task Run_Forwards_Approved_Request_To_Service_And_Returns_Result()
    {
        var (controller, service) = Build(enabled: true);
        service.NextResult = new ClaimMigrationResult
        {
            DryRun = false,
            DocumentsRead = 5,
            DocumentsWritten = 5,
            Outcome = "success",
        };

        var response = await controller.Run(
            new ClaimMigrationRequest { DryRun = false }, default);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ClaimMigrationResult>()
            .Which.DocumentsWritten.Should().Be(5);
        service.LastRequest!.DryRun.Should().BeFalse();
    }

    [Fact]
    public async Task Run_Returns_409_When_Service_Reports_Run_In_Progress()
    {
        var (controller, service) = Build(enabled: true);
        service.NextException = new MigrationAlreadyRunningException();

        var response = await controller.Run(new ClaimMigrationRequest(), default);

        response.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Run_Resolves_ActorId_From_X_User_Id_Header_When_No_Sub_Claim()
    {
        var (controller, service) = Build(enabled: true);
        controller.HttpContext.Request.Headers["X-User-Id"] = "ops@cho";
        service.NextResult = new ClaimMigrationResult { Outcome = "success" };

        await controller.Run(new ClaimMigrationRequest(), default);

        service.LastRequest!.ActorId.Should().Be("ops@cho");
    }

    [Fact]
    public async Task Run_Falls_Back_To_Synthetic_ActorId_When_No_Principal()
    {
        var (controller, service) = Build(enabled: true);
        service.NextResult = new ClaimMigrationResult { Outcome = "success" };

        await controller.Run(new ClaimMigrationRequest(), default);

        service.LastRequest!.ActorId.Should().Be("admin:claims-cosmos-migration");
    }

    [Fact]
    public void GetStatus_Returns_503_When_Feature_Flag_Is_Off()
    {
        var (controller, _) = Build(enabled: false);

        var response = controller.GetStatus();

        var status = response.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public void GetStatus_Returns_Snapshot_From_Service_When_Enabled()
    {
        var (controller, service) = Build(enabled: true);
        service.NextStatus = new ClaimMigrationStatus
        {
            MigrationsEnabled = true,
            SourceContainer = "Claims",
            TargetContainer = "ClaimsV2",
            BatchSize = 100,
            IsRunning = false,
        };

        var response = controller.GetStatus();

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ClaimMigrationStatus>()
            .Which.TargetContainer.Should().Be("ClaimsV2");
    }

    private static (AdminMigrationController controller, RecordingService service) Build(bool enabled)
    {
        var service = new RecordingService();
        var options = new ClaimMigrationOptions
        {
            MigrationsEnabled = enabled,
            SourceContainerName = "Claims",
            TargetContainerName = "ClaimsV2",
            BatchSize = 100,
        };
        var monitor = new SingleValueOptionsMonitor<ClaimMigrationOptions>(options);
        var controller = new AdminMigrationController(
            service, monitor, NullLogger<AdminMigrationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (controller, service);
    }

    private sealed class RecordingService : IClaimMigrationService
    {
        public ClaimMigrationResult NextResult { get; set; } = new();
        public ClaimMigrationStatus NextStatus { get; set; } = new();
        public Exception? NextException { get; set; }
        public ClaimMigrationRequest? LastRequest { get; private set; }

        public Task<ClaimMigrationResult> RunAsync(ClaimMigrationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            if (NextException is not null) throw NextException;
            return Task.FromResult(NextResult);
        }

        public ClaimMigrationStatus GetStatus() => NextStatus;
    }

    private sealed class SingleValueOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public SingleValueOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
