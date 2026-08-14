using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.ReferenceData.Domain;
using Xunit;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

public class FeeScheduleProvenanceTests
{
    [Fact]
    public void Schedule_preserves_source_and_ownership_metadata()
    {
        var schedule = new FeeSchedule
        {
            SourceType = FeeScheduleSourceType.PublicGovernment,
            SourceId = "cms-mpfs",
            SourceVersion = "2026-Q1",
            PayerId = "CMS",
            NetworkId = "medicare",
            Jurisdiction = "US-AZ",
            CodeSystem = "HCPCS",
            Checksum = "sha256:fixture",
            IsGlobal = true,
            LicenseClassification = LicenseClassification.Public
        };

        Assert.Equal(FeeScheduleSourceType.PublicGovernment, schedule.SourceType);
        Assert.Equal("2026-Q1", schedule.SourceVersion);
        Assert.Equal("US-AZ", schedule.Jurisdiction);
        Assert.Equal(LicenseClassification.Public, schedule.LicenseClassification);
    }
}
