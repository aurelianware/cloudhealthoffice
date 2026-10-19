using System.Reflection;
using BenefitPlanService.Controllers;
using BenefitPlanService.Models;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Controllers;

/// <summary>
/// <c>PUT/GET api/v1/plans/{planId}/default-fee-schedule</c>: a tenant-scoped,
/// idempotent upsert of the plan's flat-rate default schedule, which the Fee
/// Schedule Engine falls back to for uncontracted providers.
/// </summary>
public sealed class PlanDefaultFeeScheduleControllerTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact]
    public async Task Put_CreatesTenantScopedPlanDefaultSchedule()
    {
        var planId = Guid.NewGuid();
        var (controller, schedules, _) = Build(TenantA, (TenantA, planId));

        var result = await controller.Put(planId, ValidRequest(), default);

        var saved = (result.Result as OkObjectResult)!.Value as FeeSchedule;
        saved.Should().NotBeNull();
        saved!.Id.Should().Be($"{TenantA}:plan-default:{planId:D}");
        saved.TenantId.Should().Be(TenantA);
        saved.DefaultForPlanId.Should().Be(planId.ToString("D"));
        saved.Type.Should().Be(FeeScheduleType.Custom);
        saved.Lines.Should().OnlyContain(l => l.RateType == FeeScheduleRateType.FlatRate);

        var revenueLine = saved.Lines.Single(l => l.RevenueCode == "0120");
        revenueLine.ProcedureCode.Should().BeEmpty();
        revenueLine.Rate.Should().Be(1800m);
        saved.Lines.Single(l => l.ProcedureCode == "99213").MultipleProcedureIndicator
            .Should().Be(MultipleProcedureIndicator.NoReduction);

        schedules.Stored.Should().ContainSingle();
    }

    [Fact]
    public async Task Put_Twice_ReplacesTheSameDocument()
    {
        var planId = Guid.NewGuid();
        var (controller, schedules, _) = Build(TenantA, (TenantA, planId));

        await controller.Put(planId, ValidRequest(), default);
        var created = schedules.Stored.Single().CreatedDate;

        var changed = ValidRequest();
        changed.Lines![0].Rate = 90m;
        await controller.Put(planId, changed, default);

        var stored = schedules.Stored.Should().ContainSingle().Subject;
        stored.Lines.Single(l => l.ProcedureCode == "99213").Rate.Should().Be(90m);
        stored.CreatedDate.Should().Be(created);
        stored.Revision.Should().Be(2, "the second PUT replaces the revision it read, so it is not a conflict");
    }

    [Fact]
    public async Task Put_PlanNotInCallerTenant_Returns404_AndWritesNothing()
    {
        var planId = Guid.NewGuid();
        var (controller, schedules, _) = Build(TenantA, (TenantB, planId));

        var result = await controller.Put(planId, ValidRequest(), default);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        schedules.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_WithoutTenant_Returns400()
    {
        var planId = Guid.NewGuid();
        var (controller, schedules, _) = Build(tenantId: null, (TenantA, planId));

        var result = await controller.Put(planId, ValidRequest(), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        schedules.Stored.Should().BeEmpty();
    }

    public static TheoryData<string, Action<PlanDefaultFeeScheduleRequest>> InvalidRequests => new()
    {
        { "no lines", r => r.Lines!.Clear() },
        { "no name", r => r.Name = " " },
        { "no effective date", r => r.EffectiveDate = null },
        { "term before effective", r => r.TermDate = r.EffectiveDate!.Value.AddDays(-1) },
        { "line without codes", r => r.Lines!.Add(new PlanDefaultFeeScheduleLineRequest { Rate = 5m }) },
        { "negative rate", r => r.Lines![0].Rate = -1m },
        { "bad revenue code", r => r.Lines!.Add(new PlanDefaultFeeScheduleLineRequest { RevenueCode = "ABCD", Rate = 5m }) },
        { "duplicate procedure", r => r.Lines!.Add(new PlanDefaultFeeScheduleLineRequest { ProcedureCode = "99213", Rate = 5m }) },
        { "duplicate revenue (leading zero)", r => r.Lines!.Add(new PlanDefaultFeeScheduleLineRequest { RevenueCode = "120", Rate = 5m }) },
        { "unknown indicator", r => r.Lines![0].MultipleProcedureIndicator = 8 },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Put_InvalidRequest_Returns400_AndWritesNothing(string because, Action<PlanDefaultFeeScheduleRequest> mutate)
    {
        var planId = Guid.NewGuid();
        var (controller, schedules, _) = Build(TenantA, (TenantA, planId));
        var request = ValidRequest();
        mutate(request);

        var result = await controller.Put(planId, request, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>(because);
        schedules.Stored.Should().BeEmpty(because);
    }

    [Fact]
    public async Task Get_ReturnsTheCallerTenantsSchedule_Only()
    {
        var planId = Guid.NewGuid();
        var (controllerA, schedules, plans) = Build(TenantA, (TenantA, planId));

        (await controllerA.Get(planId, default)).Result.Should().BeOfType<NotFoundResult>();

        await controllerA.Put(planId, ValidRequest(), default);
        var found = (await controllerA.Get(planId, default)).Result as OkObjectResult;
        (found!.Value as FeeSchedule)!.DefaultForPlanId.Should().Be(planId.ToString("D"));

        var controllerB = Controller(schedules, plans, TenantB);
        (await controllerB.Get(planId, default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void Put_RequiresSettingsManage_HeldByTenantAdmin_NotByClaimsExaminer()
    {
        var permissions = Declared(nameof(PlanDefaultFeeScheduleController.Put));
        permissions.Should().Equal("settings:manage");
        RoleHolds(ChoRolePermissions.TenantAdmin, permissions).Should().BeTrue();
        RoleHolds(ChoRolePermissions.ClaimsExaminer, permissions).Should().BeFalse();
    }

    [Fact]
    public void Get_RequiresBenefitsRead()
        => Declared(nameof(PlanDefaultFeeScheduleController.Get)).Should().Equal("benefits:read");

    private static List<string> Declared(string actionName)
        => typeof(PlanDefaultFeeScheduleController).GetMethod(actionName)!
            .GetCustomAttributes<RequirePermissionAttribute>()
            .SelectMany(a => a.Permission.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();

    private static bool RoleHolds(string role, IEnumerable<string> required)
        => required.Any(r => ChoRolePermissions.ForRole(role).Any(granted => ChoRolePermissions.Matches(granted, r)));

    private static PlanDefaultFeeScheduleRequest ValidRequest() => new()
    {
        Name = "MCC Plan Default Fee Schedule",
        EffectiveDate = new DateTime(2024, 1, 1),
        Lines =
        [
            new() { ProcedureCode = "99213", Rate = 85m, MultipleProcedureIndicator = 0 },
            new() { ProcedureCode = "27447", Rate = 5100m, MultipleProcedureIndicator = 2 },
            new() { ProcedureCode = "", RevenueCode = "0120", Rate = 1800m, MultipleProcedureIndicator = 9 },
            new() { RevenueCode = "0300", Rate = 120m, MultipleProcedureIndicator = 9 },
        ],
    };

    private static (PlanDefaultFeeScheduleController, RecordingFeeScheduleRepo, InMemoryBenefitPlanRepository) Build(
        string? tenantId, (string Tenant, Guid PlanId) existingPlan)
    {
        var schedules = new RecordingFeeScheduleRepo();
        var plans = new InMemoryBenefitPlanRepository();
        plans.CreateAsync(new BenefitPlan
        {
            Id = existingPlan.PlanId.ToString("D"),
            TenantId = existingPlan.Tenant,
            PlanId = "MCC-LOCAL-TEST",
            PlanName = "MCC Local Validation PPO",
            EffectiveDate = new DateTime(2025, 1, 1),
        }).GetAwaiter().GetResult();
        return (Controller(schedules, plans, tenantId), schedules, plans);
    }

    private static PlanDefaultFeeScheduleController Controller(
        RecordingFeeScheduleRepo schedules, InMemoryBenefitPlanRepository plans, string? tenantId)
    {
        var controller = new PlanDefaultFeeScheduleController(
            schedules, plans, NullLogger<PlanDefaultFeeScheduleController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        if (tenantId is not null)
            controller.HttpContext.Items["TenantId"] = tenantId;
        return controller;
    }

    private sealed class RecordingFeeScheduleRepo : IFeeScheduleRepository
    {
        private readonly Dictionary<(string, string), FeeSchedule> _docs = new();
        public IReadOnlyList<FeeSchedule> Stored => _docs.Values.ToList();

        public Task<FeeSchedule?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
            => Task.FromResult(_docs.GetValueOrDefault((tenantId, id)));

        public Task<FeeSchedule?> GetDefaultForPlanAsync(string tenantId, string planId, DateTime serviceDate, CancellationToken ct = default)
            => Task.FromResult(_docs.Values.FirstOrDefault(s => s.TenantId == tenantId && s.DefaultForPlanId == planId));

        public Task<FeeScheduleLine?> GetLineAsync(string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct = default)
            => Task.FromResult<FeeScheduleLine?>(null);

        /// <summary>Like the MongoDB store: conditional on the revision read (0 = create), stores it + 1.</summary>
        public Task<FeeSchedule> UpsertAsync(FeeSchedule schedule, CancellationToken ct = default)
        {
            var stored = _docs.GetValueOrDefault((schedule.TenantId, schedule.Id))?.Revision ?? 0;
            if (stored != schedule.Revision)
                throw new FeeScheduleConcurrencyException("Fee schedule", schedule.Id, schedule.Revision);
            schedule.Revision++;
            _docs[(schedule.TenantId, schedule.Id)] = schedule;
            return Task.FromResult(schedule);
        }

        public Task<IReadOnlyList<FeeSchedule>> ListAsync(string tenantId, int page = 1, int pageSize = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeSchedule>>(_docs.Values.Where(s => s.TenantId == tenantId).ToList());
    }
}
