using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CloudHealthOffice.NachaTransmission;

/// <summary>
/// What a receipt says about a NACHA file, read from the file itself (the
/// bytes that are sent), never from what the caller believes it put in:
/// byte size, SHA-256, entry count and debit/credit totals from the entry
/// detail records (type 6). No account or routing number is kept.
/// </summary>
public sealed record NachaFileFacts(long ByteSize, string Sha256, int EntryCount, decimal TotalDebitAmount, decimal TotalCreditAmount)
{
    /// <summary>NACHA files are ASCII.</summary>
    public static byte[] Encode(string content) => Encoding.ASCII.GetBytes(content);

    public static NachaFileFacts From(string content) => From(Encode(content));

    public static NachaFileFacts From(byte[] bytes)
    {
        var entryCount = 0;
        long debitCents = 0, creditCents = 0;

        var text = Encoding.ASCII.GetString(bytes);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 39 || line[0] != '6')
                continue;

            entryCount++;
            if (!long.TryParse(line.AsSpan(29, 10), NumberStyles.None, CultureInfo.InvariantCulture, out var cents))
                throw new InvalidOperationException($"NACHA entry {entryCount} has an unreadable amount.");

            // Transaction code (positions 2-3): last digit 2/3/4 is a credit
            // (live, prenote, zero-dollar); 5/7/8/9 is a debit.
            switch (line[2])
            {
                case '2' or '3' or '4':
                    creditCents += cents;
                    break;
                case '5' or '7' or '8' or '9':
                    debitCents += cents;
                    break;
                default:
                    throw new InvalidOperationException($"NACHA entry {entryCount} has an unknown transaction code.");
            }
        }

        return new NachaFileFacts(
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            entryCount,
            debitCents / 100m,
            creditCents / 100m);
    }
}
