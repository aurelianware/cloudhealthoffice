using CloudHealthOffice.Infrastructure.ReferenceData;
using FluentAssertions;

namespace CloudHealthOffice.Infrastructure.Tests.ReferenceData;

public class Icd10CmCodeTests
{
    [Theory]
    [InlineData("J189", "J18.9")]
    [InlineData("E119", "E11.9")]
    [InlineData("M75110", "M75.110")]
    [InlineData("S72001A", "S72.001A")]
    [InlineData("I10", "I10")]
    [InlineData("J18.9", "J18.9")]
    [InlineData(" e1165 ", "E11.65")]
    public void A_code_is_spelled_the_way_fhir_spells_it(string stored, string fhir)
        => Icd10CmCode.ToFhir(stored).Should().Be(fhir);

    [Theory]
    [InlineData("4019")]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    public void A_value_that_is_not_an_icd10cm_code_is_left_alone(string value)
        => Icd10CmCode.ToFhir(value).Should().Be(value);
}
