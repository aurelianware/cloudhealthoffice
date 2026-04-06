using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Output;

/// <summary>
/// Writes the corpus as FHIR R4 Bundle resources.
/// Maps synthetic claims to simplified FHIR Claim resources for CMS-0057-F compliance testing.
/// </summary>
public class FhirBundleWriter : ICorpusWriter
{
    private readonly string _outputPath;
    private readonly int _claimsPerBundle;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<object> _currentBundle = new();
    private int _bundleCounter;
    private int _totalWritten;

    /// <summary>
    /// Initializes a new instance of the <see cref="FhirBundleWriter"/> class.
    /// </summary>
    /// <param name="outputPath">Root directory for FHIR bundle output.</param>
    /// <param name="claimsPerBundle">Number of claims per FHIR bundle. Default is 1,000.</param>
    public FhirBundleWriter(string outputPath, int claimsPerBundle = 1_000)
    {
        _outputPath = outputPath;
        _claimsPerBundle = claimsPerBundle;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputPath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteClaimAsync(SyntheticClaim claim, CancellationToken cancellationToken = default)
    {
        var fhirClaim = MapToFhirClaim(claim);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            _currentBundle.Add(fhirClaim);
            _totalWritten++;

            if (_currentBundle.Count >= _claimsPerBundle)
            {
                await FlushBundleAsync(cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task FinalizeAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (_currentBundle.Count > 0)
            {
                await FlushBundleAsync(cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private static object MapToFhirClaim(SyntheticClaim claim)
    {
        return new
        {
            resourceType = "Claim",
            id = claim.ClaimId,
            status = "active",
            type = new
            {
                coding = new[]
                {
                    new
                    {
                        system = "http://terminology.hl7.org/CodeSystem/claim-type",
                        code = claim.ClaimType.ToLowerInvariant() switch
                        {
                            "professional" => "professional",
                            "institutional" => "institutional",
                            "dental" => "oral",
                            _ => "professional"
                        }
                    }
                }
            },
            use = "claim",
            patient = new { reference = $"Patient/{claim.Member.MemberId}" },
            created = claim.DateReceived.ToString("yyyy-MM-dd"),
            provider = new { reference = $"Practitioner/{claim.BillingProvider.Npi}" },
            priority = new
            {
                coding = new[] { new { code = "normal" } }
            },
            diagnosis = new[]
            {
                new
                {
                    sequence = 1,
                    diagnosisCodeableConcept = new
                    {
                        coding = new[]
                        {
                            new
                            {
                                system = "http://hl7.org/fhir/sid/icd-10-cm",
                                code = claim.PrimaryDiagnosisCode
                            }
                        }
                    }
                }
            },
            item = claim.Lines.Select(l => new
            {
                sequence = l.LineNumber,
                productOrService = new
                {
                    coding = new[]
                    {
                        new
                        {
                            system = "http://www.ama-assn.org/go/cpt",
                            code = l.ProcedureCode
                        }
                    }
                },
                quantity = new { value = l.Units },
                unitPrice = new { value = l.ChargeAmount, currency = "USD" }
            }).ToArray(),
            total = new { value = claim.TotalCharges, currency = "USD" }
        };
    }

    private async Task FlushBundleAsync(CancellationToken cancellationToken)
    {
        var bundle = new
        {
            resourceType = "Bundle",
            id = $"mcc-bundle-{_bundleCounter:D5}",
            type = "collection",
            total = _currentBundle.Count,
            entry = _currentBundle.Select(c => new
            {
                resource = c
            }).ToArray()
        };

        var fileName = $"fhir-bundle-{_bundleCounter:D5}.json";
        var filePath = Path.Combine(_outputPath, fileName);
        var json = JsonSerializer.Serialize(bundle, _jsonOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);

        _bundleCounter++;
        _currentBundle.Clear();
    }
}
