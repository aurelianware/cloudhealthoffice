using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccMemberSeedStatusTests
{
    [Theory]
    [InlineData(null, MccMemberSeedStatus.Active)]
    [InlineData("", MccMemberSeedStatus.Active)]
    [InlineData("Active", MccMemberSeedStatus.Active)]
    [InlineData("active", MccMemberSeedStatus.Active)]
    [InlineData("Pending", MccMemberSeedStatus.Pending)]
    [InlineData("Terminated", MccMemberSeedStatus.Terminated)]
    [InlineData("Suspended", MccMemberSeedStatus.Suspended)]
    [InlineData("COBRA", MccMemberSeedStatus.Cobra)]
    [InlineData("Unexpected", MccMemberSeedStatus.Active)]
    public void ToMemberServiceStatus_maps_synthetic_enrollment_status_to_member_service_enum(
        string? status,
        string expected)
    {
        Assert.Equal(expected, MccMemberSeedStatus.ToMemberServiceStatus(status));
    }
}
