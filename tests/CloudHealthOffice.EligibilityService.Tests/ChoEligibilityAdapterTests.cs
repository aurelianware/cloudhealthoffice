using System.Net;
using System.Text;
using System.Text.Json;
using EligibilityService.Adapters;
using EligibilityService.Models;
using EligibilityService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests;

public class ChoEligibilityAdapterTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IConfiguration _configuration;
    private readonly ILogger<ChoEligibilityAdapter> _logger = Substitute.For<ILogger<ChoEligibilityAdapter>>();

    public ChoEligibilityAdapterTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:CoverageService"] = "http://localhost:9000/api/v1",
                ["Services:BenefitPlanService"] = "http://localhost:9001/api/v1",
            })
            .Build();
    }

    private ChoEligibilityAdapter CreateAdapter(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("EligibilityDefault").Returns(new HttpClient(handler));
        return new ChoEligibilityAdapter(factory, _configuration, _logger);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Coverage deserialization — the bug that caused the crash
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyEligibility_CoverageReturnsArray_DeserializesFirstActive()
    {
        // Coverage service returns List<Coverage> with Status as int (1=Active)
        var coverageArray = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "cov-001",
                memberId = "MBR-001",
                groupNumber = "GRP-100",
                planId = "PLAN-PPO-2025",
                coverageLevel = "FAM",
                insuranceLineCode = "HLT",
                effectiveDate = "2025-01-01",
                terminationDate = (string?)null,
                status = 1, // Active
                lineOfBusiness = 1 // Commercial
            }
        }, JsonOpts);

        var benefitsArray = "[]";
        var accumulationJson = "null";

        var handler = new SequenceHandler(new[]
        {
            // 1. Tenant service call (returns 500, adapter ignores this)
            // 1. Coverage /active endpoint
            new FakeResponse(HttpStatusCode.OK, coverageArray),
            // 2. Benefits endpoint
            new FakeResponse(HttpStatusCode.OK, benefitsArray),
            // 3. Accumulation endpoint
            new FakeResponse(HttpStatusCode.NotFound, ""),
            // 4. COB endpoint
            new FakeResponse(HttpStatusCode.NotFound, ""),
        });

        var adapter = CreateAdapter(handler);

        var result = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = "test-tenant",
            SubscriberId = "MBR-001",
            ServiceDate = new DateTime(2025, 6, 15),
            ServiceTypeCode = "30"
        });

        Assert.True(result.IsEligible);
        Assert.Equal("1", result.StatusCode);
        Assert.Equal("GRP-100", result.GroupNumber);
        Assert.Equal("PLAN-PPO-2025", result.PlanId);
        Assert.Equal("FAM", result.CoverageLevel);
    }

    [Fact]
    public async Task VerifyEligibility_CoverageReturnsTerminated_ReturnsNotEligible()
    {
        var coverageArray = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "cov-001",
                memberId = "MBR-001",
                groupNumber = "GRP-100",
                planId = "PLAN-PPO-2025",
                coverageLevel = "FAM",
                effectiveDate = "2024-01-01",
                terminationDate = "2024-12-31",
                status = 3, // Terminated
                lineOfBusiness = 1
            }
        }, JsonOpts);

        var handler = new SequenceHandler(new[]
        {
            new FakeResponse(HttpStatusCode.OK, coverageArray),
        });

        var adapter = CreateAdapter(handler);

        var result = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = "test-tenant",
            SubscriberId = "MBR-001",
            ServiceDate = new DateTime(2025, 6, 15),
        });

        Assert.False(result.IsEligible);
        Assert.Equal("6", result.StatusCode);
        Assert.Contains("No active coverage", result.RejectionReason);
    }

    [Fact]
    public async Task VerifyEligibility_CoverageReturnsEmpty_ReturnsNotEligible()
    {
        var handler = new SequenceHandler(new[]
        {
            new FakeResponse(HttpStatusCode.OK, "[]"),
        });

        var adapter = CreateAdapter(handler);

        var result = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = "test-tenant",
            SubscriberId = "MBR-NONE",
            ServiceDate = DateTime.UtcNow,
        });

        Assert.False(result.IsEligible);
    }

    [Fact]
    public async Task VerifyEligibility_Coverage404_ReturnsNotEligible()
    {
        var handler = new SequenceHandler(new[]
        {
            new FakeResponse(HttpStatusCode.NotFound, "{}"),
        });

        var adapter = CreateAdapter(handler);

        var result = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = "test-tenant",
            SubscriberId = "MBR-NONE",
            ServiceDate = DateTime.UtcNow,
        });

        Assert.False(result.IsEligible);
    }

    [Fact]
    public async Task VerifyEligibility_CobraCoverage_IsConsideredActive()
    {
        var coverageArray = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "cov-cobra",
                memberId = "MBR-002",
                groupNumber = "GRP-200",
                planId = "PLAN-COBRA",
                coverageLevel = "EMP",
                effectiveDate = "2025-01-01",
                terminationDate = (string?)null,
                status = 5, // COBRA
                lineOfBusiness = 1
            }
        }, JsonOpts);

        var handler = new SequenceHandler(new[]
        {
            new FakeResponse(HttpStatusCode.OK, coverageArray),
            new FakeResponse(HttpStatusCode.OK, "[]"), // benefits
            new FakeResponse(HttpStatusCode.NotFound, ""), // accumulation
            new FakeResponse(HttpStatusCode.NotFound, ""), // COB
        });

        var adapter = CreateAdapter(handler);

        var result = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = "test-tenant",
            SubscriberId = "MBR-002",
            ServiceDate = DateTime.UtcNow,
        });

        Assert.True(result.IsEligible);
        Assert.Equal("PLAN-COBRA", result.PlanId);
    }

    [Fact]
    public async Task VerifyEligibility_MultipleEntriesOnlyOneActive_PicksActive()
    {
        var coverageArray = JsonSerializer.Serialize(new[]
        {
            new { id = "cov-old", memberId = "MBR-001", groupNumber = "GRP-OLD",
                  planId = "PLAN-OLD", coverageLevel = "EMP",
                  effectiveDate = "2023-01-01", terminationDate = "2023-12-31",
                  status = 3, lineOfBusiness = 1 },
            new { id = "cov-new", memberId = "MBR-001", groupNumber = "GRP-NEW",
                  planId = "PLAN-NEW", coverageLevel = "FAM",
                  effectiveDate = "2025-01-01", terminationDate = (string?)null,
                  status = 1, lineOfBusiness = 1 },
        }, JsonOpts);

        var handler = new SequenceHandler(new[]
        {
            new FakeResponse(HttpStatusCode.OK, coverageArray),
            new FakeResponse(HttpStatusCode.OK, "[]"),
            new FakeResponse(HttpStatusCode.NotFound, ""),
            new FakeResponse(HttpStatusCode.NotFound, ""),
        });

        var adapter = CreateAdapter(handler);

        var result = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = "test-tenant",
            SubscriberId = "MBR-001",
            ServiceDate = new DateTime(2025, 6, 15),
        });

        Assert.True(result.IsEligible);
        Assert.Equal("PLAN-NEW", result.PlanId);
        Assert.Equal("GRP-NEW", result.GroupNumber);
    }

    [Fact]
    public async Task VerifyEligibility_TerminatedCoverage_ServiceDateBeforeTermDate_IsEligible()
    {
        // A terminated (incl. retro-termed) coverage was in force for its span;
        // a DOS on or before the termination date must still be eligible.
        var coverageArray = JsonSerializer.Serialize(new[]
        {
            new { id = "cov-term", memberId = "MBR-001", groupNumber = "GRP-100",
                  planId = "PLAN-PPO-2025", coverageLevel = "FAM",
                  effectiveDate = "2025-01-01", terminationDate = "2025-06-30",
                  status = 3, lineOfBusiness = 1 },
        }, JsonOpts);

        var handler = new SequenceHandler(new[]
        {
            new FakeResponse(HttpStatusCode.OK, coverageArray),
            new FakeResponse(HttpStatusCode.OK, "[]"),
            new FakeResponse(HttpStatusCode.NotFound, ""),
            new FakeResponse(HttpStatusCode.NotFound, ""),
        });

        var adapter = CreateAdapter(handler);

        var result = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = "test-tenant",
            SubscriberId = "MBR-001",
            ServiceDate = new DateTime(2025, 6, 30),
        });

        Assert.True(result.IsEligible);
        Assert.Equal("1", result.StatusCode);
        Assert.Equal("PLAN-PPO-2025", result.PlanId);
        Assert.Equal(new DateTime(2025, 6, 30), result.CoverageEndDate);
    }

    [Fact]
    public async Task VerifyEligibility_TerminatedCoverageWithoutTerminationDate_ReturnsNotEligible()
    {
        // Status=Terminated with no end date fails closed, not open-ended.
        var coverageArray = JsonSerializer.Serialize(new[]
        {
            new { id = "cov-term", memberId = "MBR-001", groupNumber = "GRP-100",
                  planId = "PLAN-PPO-2025", coverageLevel = "FAM",
                  effectiveDate = "2025-01-01", terminationDate = (string?)null,
                  status = 3, lineOfBusiness = 1 },
        }, JsonOpts);

        var handler = new SequenceHandler(new[]
        {
            new FakeResponse(HttpStatusCode.OK, coverageArray),
        });

        var adapter = CreateAdapter(handler);

        var result = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = "test-tenant",
            SubscriberId = "MBR-001",
            ServiceDate = new DateTime(2025, 6, 15),
        });

        Assert.False(result.IsEligible);
        Assert.Equal("6", result.StatusCode);
    }

    [Theory]
    [InlineData(4)] // Suspended
    public async Task VerifyEligibility_SuspendedCoverage_ReturnsNotEligible(int status)
    {
        var coverageArray = JsonSerializer.Serialize(new[]
        {
            new { id = "cov-x", memberId = "MBR-001", groupNumber = "GRP-100",
                  planId = "PLAN-PPO-2025", coverageLevel = "FAM",
                  effectiveDate = "2025-01-01", terminationDate = (string?)null,
                  status, lineOfBusiness = 1 },
        }, JsonOpts);

        var handler = new SequenceHandler(new[]
        {
            new FakeResponse(HttpStatusCode.OK, coverageArray),
        });

        var adapter = CreateAdapter(handler);

        var result = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = "test-tenant",
            SubscriberId = "MBR-001",
            ServiceDate = new DateTime(2025, 6, 15),
        });

        Assert.False(result.IsEligible);
        Assert.Equal("6", result.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Coverage selection — insurance line of the service type, then group
    // (shared with EligibilityServiceImpl.SelectCoverage)
    // ═══════════════════════════════════════════════════════════════════

    private static object Line(string id, string plan, string? line, string group = "GRP-100",
        string effective = "2025-01-01") =>
        new { id, memberId = "MBR-001", groupNumber = group, planId = plan, coverageLevel = "EMP",
              insuranceLineCode = line, effectiveDate = effective, terminationDate = (string?)null,
              status = "Active", lineOfBusiness = "Commercial" };

    private Task<EligibilityAdapterResponse> VerifyAsync(object[] coverages, string serviceType, string? group = null) =>
        CreateAdapter(new SequenceHandler(new[]
        {
            new FakeResponse(HttpStatusCode.OK, JsonSerializer.Serialize(coverages, JsonOpts)),
            new FakeResponse(HttpStatusCode.OK, "[]"),
            new FakeResponse(HttpStatusCode.NotFound, ""),
            new FakeResponse(HttpStatusCode.NotFound, ""),
        })).VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = "test-tenant",
            SubscriberId = "MBR-001",
            GroupNumber = group,
            ServiceDate = new DateTime(2025, 6, 15),
            ServiceTypeCode = serviceType
        });

    [Theory]
    [InlineData("30", "PLAN-HLT")]
    [InlineData("35", "PLAN-DEN")]
    [InlineData("AN", "PLAN-VIS")]
    public async Task VerifyEligibility_MixedLines_UsesTheServiceTypesInsuranceLine(string serviceType, string expectedPlan)
    {
        // Dental first, so "first in-force entry" would answer a medical question with the dental plan.
        var result = await VerifyAsync(new[]
        {
            Line("cov-den", "PLAN-DEN", "DEN"),
            Line("cov-hlt", "PLAN-HLT", "HLT"),
            Line("cov-vis", "PLAN-VIS", "VIS")
        }, serviceType);

        Assert.True(result.IsEligible);
        Assert.Equal(expectedPlan, result.PlanId);
    }

    [Fact]
    public async Task VerifyEligibility_DentalOnlyMember_IsNotEligibleForMedical_ButIsForDental()
    {
        var dentalOnly = new[] { Line("cov-den", "PLAN-DEN", "DEN") };

        var medical = await VerifyAsync(dentalOnly, "30");
        Assert.False(medical.IsEligible);
        Assert.Equal("6", medical.StatusCode);

        var dental = await VerifyAsync(dentalOnly, "35");
        Assert.True(dental.IsEligible);
        Assert.Equal("PLAN-DEN", dental.PlanId);
    }

    [Fact]
    public async Task VerifyEligibility_PrefersRequestedGroup_ThenLatestEffective_LineNullIsHealth()
    {
        var coverages = new[]
        {
            Line("cov-a", "PLAN-A", null, group: "GRP-A", effective: "2025-03-01"),
            Line("cov-b", "PLAN-B", "HLT", group: "GRP-B", effective: "2025-01-01")
        };

        Assert.Equal("PLAN-B", (await VerifyAsync(coverages, "30", group: "GRP-B")).PlanId);
        Assert.Equal("PLAN-A", (await VerifyAsync(coverages, "30")).PlanId);
    }

    [Fact]
    public void Platform_ReturnsCho()
    {
        var handler = new SequenceHandler(Array.Empty<FakeResponse>());
        var adapter = CreateAdapter(handler);
        Assert.Equal("cho", adapter.Platform);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test helpers
    // ═══════════════════════════════════════════════════════════════════

    private record FakeResponse(HttpStatusCode Status, string Body);

    private class SequenceHandler : HttpMessageHandler
    {
        private readonly FakeResponse[] _responses;
        private int _index;

        public SequenceHandler(FakeResponse[] responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = _index < _responses.Length
                ? _responses[_index]
                : new FakeResponse(HttpStatusCode.InternalServerError, "");
            _index++;

            var msg = new HttpResponseMessage(resp.Status);
            if (!string.IsNullOrEmpty(resp.Body))
                msg.Content = new StringContent(resp.Body, Encoding.UTF8, "application/json");
            return Task.FromResult(msg);
        }
    }
}
