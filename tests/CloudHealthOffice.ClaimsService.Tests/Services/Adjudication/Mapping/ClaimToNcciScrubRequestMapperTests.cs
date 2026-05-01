using ClaimsService.Models;
using ClaimsService.Services.Adjudication.Mapping;
using EngineModels = CloudHealthOffice.NcciEngine.Models;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Mapping;

/// <summary>
/// Capability 5.7 — mapping fidelity coverage for
/// <see cref="ClaimToNcciScrubRequestMapper"/>: claim-type translation,
/// service-line filtering against engine validation, modifier
/// pass-through, effective-date selection (earliest service date wins).
/// </summary>
public class ClaimToNcciScrubRequestMapperTests
{
    [Theory]
    [InlineData(ClaimType.Professional, "837P")]
    [InlineData(ClaimType.Institutional, "837I")]
    [InlineData(ClaimType.Dental, "837D")]
    public void MapClaimType_routes_each_enum_to_engine_string(ClaimType type, string expected)
    {
        Assert.Equal(expected, ClaimToNcciScrubRequestMapper.MapClaimType(type));
    }

    [Fact]
    public void MapClaimType_unknown_value_falls_back_to_837P()
    {
        Assert.Equal("837P", ClaimToNcciScrubRequestMapper.MapClaimType((ClaimType)999));
    }

    [Fact]
    public void Map_populates_top_level_request_fields()
    {
        var claim = NewClaim();
        var request = ClaimToNcciScrubRequestMapper.Map(claim);

        Assert.Equal("tenant-1", request.TenantId);
        Assert.Equal("ver-1", request.ClaimId);
        Assert.Equal("837P", request.ClaimType);
        Assert.Equal(2, request.ServiceLines.Count);
    }

    [Fact]
    public void Map_propagates_modifiers_and_units_and_pos_to_engine_line()
    {
        var claim = NewClaim();
        var request = ClaimToNcciScrubRequestMapper.Map(claim);

        var line1 = request.ServiceLines.First(l => l.LineNumber == 1);
        Assert.Equal("99213", line1.ProcedureCode);
        Assert.Equal(1m, line1.Units);
        Assert.Equal("11", line1.PlaceOfServiceCode);
        Assert.Single(line1.Modifiers);
        Assert.Equal("59", line1.Modifiers[0]);

        var line2 = request.ServiceLines.First(l => l.LineNumber == 2);
        Assert.Empty(line2.Modifiers);
    }

    [Fact]
    public void Map_drops_lines_with_invalid_procedure_codes()
    {
        var claim = NewClaim();
        claim.ClaimLines.Add(new AdapterClaimLine
        {
            LineNumber = 3,
            ProcedureCode = "BAD",     // not 5 chars — engine [StringLength(5,5)] would reject
            Units = 1m,
            ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
        });
        claim.ClaimLines.Add(new AdapterClaimLine
        {
            LineNumber = 4,
            ProcedureCode = null!,     // null — filtered before engine call
            Units = 1m,
            ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
        });

        var request = ClaimToNcciScrubRequestMapper.Map(claim);

        Assert.Equal(2, request.ServiceLines.Count); // only the original two valid lines
        Assert.DoesNotContain(request.ServiceLines, l => l.LineNumber == 3 || l.LineNumber == 4);
    }

    [Fact]
    public void Map_drops_lines_with_units_outside_engine_range()
    {
        var claim = NewClaim();
        claim.ClaimLines.Add(new AdapterClaimLine
        {
            LineNumber = 3, ProcedureCode = "99215", Units = 0m,    // below engine [Range(0.01, 9999)]
            ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
        });
        claim.ClaimLines.Add(new AdapterClaimLine
        {
            LineNumber = 4, ProcedureCode = "99216", Units = 10000m, // above engine [Range(0.01, 9999)]
            ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
        });

        var request = ClaimToNcciScrubRequestMapper.Map(claim);

        Assert.Equal(2, request.ServiceLines.Count);
    }

    [Fact]
    public void EffectiveDate_uses_earliest_line_service_date()
    {
        var claim = NewClaim();
        claim.ClaimLines.Clear();
        claim.ClaimLines.Add(new AdapterClaimLine
        {
            LineNumber = 1, ProcedureCode = "99213", Units = 1m,
            ServiceDateFrom = new DateTime(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc),
        });
        claim.ClaimLines.Add(new AdapterClaimLine
        {
            LineNumber = 2, ProcedureCode = "99214", Units = 1m,
            ServiceDateFrom = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Utc), // earlier
        });
        claim.ClaimLines.Add(new AdapterClaimLine
        {
            LineNumber = 3, ProcedureCode = "99215", Units = 1m,
            ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
        });

        var request = ClaimToNcciScrubRequestMapper.Map(claim);

        Assert.Equal(new DateOnly(2026, 4, 14), request.EffectiveDate);
    }

    [Fact]
    public void EffectiveDate_falls_back_to_header_when_no_lines_remain()
    {
        var claim = NewClaim();
        claim.ClaimLines.Clear();
        claim.ClaimLines.Add(new AdapterClaimLine
        {
            LineNumber = 1, ProcedureCode = "ABC", Units = 1m, // filtered by mapper
            ServiceDateFrom = new DateTime(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc),
        });

        var request = ClaimToNcciScrubRequestMapper.Map(claim);

        Assert.Empty(request.ServiceLines);
        Assert.Equal(new DateOnly(2026, 4, 15), request.EffectiveDate); // claim.ServiceDateFrom
    }

    [Fact]
    public void IsLineEngineValid_accepts_5_char_code_and_in_range_units()
    {
        var line = new AdapterClaimLine
        {
            ProcedureCode = "99213",
            Units = 1m,
            ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
        };
        Assert.True(ClaimToNcciScrubRequestMapper.IsLineEngineValid(line));
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("123456")]
    [InlineData("")]
    [InlineData(null)]
    public void IsLineEngineValid_rejects_invalid_procedure_codes(string? code)
    {
        var line = new AdapterClaimLine
        {
            ProcedureCode = code!,
            Units = 1m,
            ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
        };
        Assert.False(ClaimToNcciScrubRequestMapper.IsLineEngineValid(line));
    }

    [Fact]
    public void IsLineEngineValid_rejects_default_service_date()
    {
        // Engine quarter resolution + same-DOS pair grouping both depend
        // on the line's ServiceDate; a default DateTime would non-
        // deterministically resolve to the current UTC quarter and could
        // group otherwise-distinct lines. Filter at the boundary.
        var line = new AdapterClaimLine
        {
            ProcedureCode = "99213",
            Units = 1m,
            ServiceDateFrom = default,
        };
        Assert.False(ClaimToNcciScrubRequestMapper.IsLineEngineValid(line));
    }

    private static AdapterClaim NewClaim() => new()
    {
        TenantId = "tenant-1",
        Id = "ver-1",
        ClaimVersionId = "ver-1",
        ClaimNumber = "CLM-MAP-1",
        MemberId = "MEM-1",
        BillingProviderNPI = "1234567890",
        ClaimType = ClaimType.Professional,
        PlaceOfServiceCode = "11",
        ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
        ClaimLines = new List<AdapterClaimLine>
        {
            new()
            {
                LineNumber = 1,
                ProcedureCode = "99213",
                Units = 1m,
                ChargeAmount = 100m,
                PlaceOfServiceCode = "11",
                Modifiers = new List<string> { "59" },
                ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                LineNumber = 2,
                ProcedureCode = "99214",
                Units = 1m,
                ChargeAmount = 100m,
                ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
            },
        },
    };
}
