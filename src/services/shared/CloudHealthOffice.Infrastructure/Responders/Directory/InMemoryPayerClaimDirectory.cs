using System.Collections.Concurrent;

namespace CloudHealthOffice.Infrastructure.Responders.Directory;

public sealed class InMemoryPayerClaimDirectory : IPayerClaimDirectory
{
    private readonly ConcurrentDictionary<string, PayerDirectoryClaim> _claims = new(StringComparer.Ordinal);

    public InMemoryPayerClaimDirectory()
    {
        foreach (var claim in ChoDemoClaimAttachmentSeed.Claims)
        {
            _claims[Key(claim.TenantId, claim.ClaimId)] = Clone(claim);
        }
    }

    public Task<PayerClaimMatch> FindAsync(PayerClaimLookup lookup, CancellationToken ct = default)
    {
        var scoped = _claims.Values.Where(c =>
            string.Equals(c.TenantId, lookup.TenantId, StringComparison.Ordinal) &&
            string.Equals(c.CanonicalPayerId, lookup.CanonicalPayerId, StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<PayerDirectoryClaim> matches;
        if (!string.IsNullOrWhiteSpace(lookup.ClaimId))
        {
            matches = scoped
                .Where(c => string.Equals(c.ClaimId, lookup.ClaimId, StringComparison.Ordinal))
                .Select(Clone)
                .ToList();
        }
        else if (!string.IsNullOrWhiteSpace(lookup.ClaimControlNumber))
        {
            matches = scoped
                .Where(c => string.Equals(c.PayerClaimControlNumber, lookup.ClaimControlNumber, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .ToList();
        }
        else if (!string.IsNullOrWhiteSpace(lookup.PatientControlNumber))
        {
            matches = scoped
                .Where(c => string.Equals(c.PatientControlNumber, lookup.PatientControlNumber, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .ToList();
        }
        else if (!string.IsNullOrWhiteSpace(lookup.AttachmentControlNumber))
        {
            matches = scoped
                .Where(c => string.Equals(c.AttachmentControlNumber, lookup.AttachmentControlNumber, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .ToList();
        }
        else
        {
            matches = Array.Empty<PayerDirectoryClaim>();
        }

        return Task.FromResult(new PayerClaimMatch(matches));
    }

    public Task MarkDocumentationReceivedAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        if (_claims.TryGetValue(Key(tenantId, claimId), out var claim))
        {
            claim.DocumentationReceived = true;
        }

        return Task.CompletedTask;
    }

    private static string Key(string tenantId, string claimId) => $"{tenantId}\u001f{claimId}";

    private static PayerDirectoryClaim Clone(PayerDirectoryClaim source) =>
        new()
        {
            TenantId = source.TenantId,
            ClaimId = source.ClaimId,
            CanonicalPayerId = source.CanonicalPayerId,
            PayerClaimControlNumber = source.PayerClaimControlNumber,
            PatientControlNumber = source.PatientControlNumber,
            AttachmentControlNumber = source.AttachmentControlNumber,
            Status = source.Status,
            DocumentationReceived = source.DocumentationReceived,
            ServiceLines = source.ServiceLines.Select(l => new PayerDirectoryClaimLine
            {
                LineNumber = l.LineNumber,
                LineControlNumber = l.LineControlNumber,
                ProcedureCode = l.ProcedureCode,
                ToothNumber = l.ToothNumber
            }).ToList()
        };
}

public sealed class UnconfiguredPayerClaimDirectory : IPayerClaimDirectory
{
    public Task<PayerClaimMatch> FindAsync(PayerClaimLookup lookup, CancellationToken ct = default) =>
        Task.FromResult(new PayerClaimMatch(Array.Empty<PayerDirectoryClaim>()));

    public Task MarkDocumentationReceivedAsync(string tenantId, string claimId, CancellationToken ct = default) =>
        Task.CompletedTask;
}
