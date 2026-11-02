using System.Text.Json;

namespace AppealsService.Services;

/// <summary>
/// The shared line-of-business JSON converter, refusing the old <c>MedicarePartD</c>
/// value: reading it as Medicare would put a Part D case on Part C clocks. Callers send
/// <c>"lineOfBusiness": "Medicare", "benefitType": "PartDDrug"</c> instead.
/// </summary>
public sealed class AppealLineOfBusinessJsonConverter() : LineOfBusinessJsonConverter(allowNumbers: false)
{
    protected override void Reject(string? text)
    {
        if (LineOfBusinessNames.IsPartD(text))
            throw new JsonException(
                $"'{text}' is no longer a line of business. Send lineOfBusiness Medicare with benefitType PartDDrug.");
    }
}
