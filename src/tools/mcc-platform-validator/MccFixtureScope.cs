using System.Security.Cryptography;
using System.Text;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

/// <summary>
/// Produces a stable fixture identity scope for repeat runs of the same
/// tenant and seed. Validation plans remain run-specific, while synthetic
/// member/provider identities can be safely verified and reused.
/// </summary>
internal static class MccFixtureScope
{
    internal static Guid Create(string tenantId, int seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var input = Encoding.UTF8.GetBytes($"{tenantId.Trim()}:{seed}");
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }
}
