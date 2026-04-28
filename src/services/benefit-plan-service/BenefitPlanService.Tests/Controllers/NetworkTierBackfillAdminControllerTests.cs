using BenefitPlanService.Controllers;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Tests.Controllers;

/// <summary>
/// Capability 5.5 — admin endpoint for the operator-driven NetworkId
/// backfill. Verifies the feature-flag tripwire, the request-shape
/// validation, and that approved requests reach the underlying service.
/// </summary>
public sealed class NetworkTierBackfillAdminControllerTests
{
    [Fact]
    public async Task BackfillNetworkTiers_Returns_503_When_Feature_Flag_Is_Off()
    {
        var (controller, _) = Build(adminEnabled: false);

        var response = await controller.BackfillNetworkTiers(
            tenantId: "tenant-a",
            request: new NetworkTierBackfillRequest(),
            ct: default);

        var status = response.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task BackfillNetworkTiers_Returns_400_When_TenantId_Missing()
    {
        var (controller, _) = Build(adminEnabled: true);

        var response = await controller.BackfillNetworkTiers(
            tenantId: "",
            request: new NetworkTierBackfillRequest(),
            ct: default);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task BackfillNetworkTiers_Returns_400_When_Mappings_Exceed_Cap()
    {
        var (controller, _) = Build(adminEnabled: true, maxMappingsPerCall: 2);

        var request = new NetworkTierBackfillRequest
        {
            Mappings = Enumerable.Range(0, 3)
                .Select(i => new NetworkTierBackfillMapping
                {
                    PlanId = $"plan-{i}",
                    TierName = "In-Network",
                    NetworkId = $"net-{i}",
                })
                .ToList(),
        };

        var response = await controller.BackfillNetworkTiers("tenant-a", request, default);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task BackfillNetworkTiers_Forwards_Approved_Request_To_Service_And_Returns_The_Result()
    {
        var (controller, service) = Build(adminEnabled: true);
        service.NextResult = new NetworkTierBackfillResult { Patched = 7 };

        var response = await controller.BackfillNetworkTiers(
            tenantId: "tenant-a",
            request: new NetworkTierBackfillRequest
            {
                Mappings = new()
                {
                    new() { PlanId = "plan-1", TierName = "In-Network", NetworkId = "net-1" },
                },
            },
            ct: default);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<NetworkTierBackfillResult>().Which.Patched.Should().Be(7);
        service.LastTenantId.Should().Be("tenant-a");
        service.LastRequest.Should().NotBeNull();
        service.LastRequest!.Mappings.Should().ContainSingle(m => m.PlanId == "plan-1");
    }

    private static (NetworkTierBackfillAdminController controller, RecordingBackfillService service) Build(
        bool adminEnabled,
        int maxMappingsPerCall = 5_000)
    {
        var service = new RecordingBackfillService();
        var options = new NetworkTierBackfillOptions
        {
            AdminBackfillEnabled = adminEnabled,
            MaxMappingsPerCall = maxMappingsPerCall,
        };
        var monitor = new SingleValueOptionsMonitor<NetworkTierBackfillOptions>(options);
        var controller = new NetworkTierBackfillAdminController(
            service, monitor, NullLogger<NetworkTierBackfillAdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (controller, service);
    }

    private sealed class RecordingBackfillService : INetworkTierBackfillService
    {
        public NetworkTierBackfillResult NextResult { get; set; } = new();
        public string? LastTenantId { get; private set; }
        public NetworkTierBackfillRequest? LastRequest { get; private set; }

        public Task<NetworkTierBackfillResult> RunTenantAsync(
            string tenantId,
            NetworkTierBackfillRequest request,
            CancellationToken ct = default)
        {
            LastTenantId = tenantId;
            LastRequest = request;
            return Task.FromResult(NextResult);
        }
    }

    private sealed class SingleValueOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public SingleValueOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
