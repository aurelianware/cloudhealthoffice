using CloudHealthOffice.Infrastructure.ReferenceData;

namespace CloudHealthOffice.Infrastructure.Tests.ReferenceData;

public sealed class SyntheticIcd10CmCatalogTests
{
    [Fact]
    public void Diagnoses_HaveUniqueCodesAndDisplays()
    {
        Assert.All(SyntheticIcd10CmCatalog.Diagnoses, diagnosis =>
        {
            Assert.False(string.IsNullOrWhiteSpace(diagnosis.Code));
            Assert.False(string.IsNullOrWhiteSpace(diagnosis.Display));
        });

        var duplicateCodes = SyntheticIcd10CmCatalog.Diagnoses
            .GroupBy(diagnosis => diagnosis.Code, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicateCodes);
    }
}
