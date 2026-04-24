using System.Text;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Output;

/// <summary>
/// Generates X12 834 (Benefit Enrollment and Maintenance) files for testing
/// the enrollment-import-service pipeline.
/// Uses ~ as segment terminator and * as element separator per X12 conventions.
/// </summary>
public class X12_834Writer
{
    private const char ElementSeparator = '*';
    private const char SegmentTerminator = '~';
    private const char SubElementSeparator = ':';
    private const int MembersPerFile = 5_000;

    private readonly string _outputDirectory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="X12_834Writer"/> class.
    /// </summary>
    /// <param name="outputDirectory">Directory for output 834 files.</param>
    /// <param name="logger">Optional logger.</param>
    public X12_834Writer(string outputDirectory, ILogger? logger = null)
    {
        _outputDirectory = outputDirectory;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Generate X12 834 files for all members, splitting into files of 5,000 members each.
    /// </summary>
    /// <param name="members">All subscriber members with dependents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of generated file paths.</returns>
    public async Task<List<string>> WriteAsync(
        List<SyntheticMember> members,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);
        var filePaths = new List<string>();
        int fileIndex = 0;
        int memberIndex = 0;

        while (memberIndex < members.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = members.Skip(memberIndex).Take(MembersPerFile).ToList();
            var fileName = $"MCC-834-{fileIndex:D4}.edi";
            var filePath = Path.Combine(_outputDirectory, fileName);

            var content = Generate834File(batch, fileIndex);
            await File.WriteAllTextAsync(filePath, content, cancellationToken);

            filePaths.Add(filePath);
            memberIndex += batch.Count;
            fileIndex++;

            _logger.LogInformation("Wrote 834 file {File} with {Count:N0} subscribers", fileName, batch.Count);
        }

        _logger.LogInformation("834 generation complete: {Files} files, {Members:N0} subscribers",
            filePaths.Count, members.Count);

        return filePaths;
    }

    private string Generate834File(List<SyntheticMember> members, int fileIndex)
    {
        var sb = new StringBuilder(1024 * 1024); // Pre-allocate ~1MB
        var controlNumber = $"{100000 + fileIndex}";
        var timestamp = DateTime.UtcNow;

        // ISA - Interchange Control Header (fixed 106 characters)
        WriteIsa(sb, controlNumber, timestamp);

        // GS - Functional Group Header
        WriteGs(sb, controlNumber, timestamp);

        // ST - Transaction Set Header
        sb.Append($"ST{ElementSeparator}834{ElementSeparator}{controlNumber}{SegmentTerminator}");

        // BGN - Beginning Segment
        sb.Append($"BGN{ElementSeparator}00{ElementSeparator}MCC-ENR-{fileIndex:D4}" +
                  $"{ElementSeparator}{timestamp:yyyyMMdd}{ElementSeparator}{timestamp:HHmmss}" +
                  $"{ElementSeparator}{ElementSeparator}{ElementSeparator}{ElementSeparator}2{SegmentTerminator}");

        // Loop 1000A - Sponsor Name (employer/sponsor)
        sb.Append($"N1{ElementSeparator}P5{ElementSeparator}MCC BENCHMARK EMPLOYER GROUP" +
                  $"{ElementSeparator}FI{ElementSeparator}741234567{SegmentTerminator}");

        // Loop 1000B - Payer Name
        sb.Append($"N1{ElementSeparator}IN{ElementSeparator}TEXAS MEDICAID MCO" +
                  $"{ElementSeparator}FI{ElementSeparator}752345678{SegmentTerminator}");

        int segmentCount = 4; // ST + BGN + 2x N1

        // Loop 2000 - Member Level Detail
        foreach (var subscriber in members)
        {
            segmentCount += WriteMemberLoop(sb, subscriber, isSubscriber: true);

            // Dependents
            foreach (var dependent in subscriber.Dependents)
            {
                segmentCount += WriteDependentLoop(sb, subscriber, dependent);
            }
        }

        // SE - Transaction Set Trailer
        segmentCount++; // SE itself
        sb.Append($"SE{ElementSeparator}{segmentCount}{ElementSeparator}{controlNumber}{SegmentTerminator}");

        // GE - Functional Group Trailer
        sb.Append($"GE{ElementSeparator}1{ElementSeparator}{controlNumber}{SegmentTerminator}");

        // IEA - Interchange Control Trailer
        sb.Append($"IEA{ElementSeparator}1{ElementSeparator}{controlNumber.PadLeft(9, '0')}{SegmentTerminator}");

        return sb.ToString();
    }

    private static void WriteIsa(StringBuilder sb, string controlNumber, DateTime timestamp)
    {
        // ISA is fixed-width: 106 characters including terminators
        // Each element is padded to exact length per ISA spec
        sb.Append("ISA");
        sb.Append(ElementSeparator); sb.Append("00");              // ISA01 - Auth Info Qualifier
        sb.Append(ElementSeparator); sb.Append("          ");      // ISA02 - Auth Info (10 spaces)
        sb.Append(ElementSeparator); sb.Append("00");              // ISA03 - Security Info Qualifier
        sb.Append(ElementSeparator); sb.Append("          ");      // ISA04 - Security Info (10 spaces)
        sb.Append(ElementSeparator); sb.Append("ZZ");              // ISA05 - Sender Qualifier
        sb.Append(ElementSeparator); sb.Append("MCCBENCHMARK   "); // ISA06 - Sender ID (15 chars)
        sb.Append(ElementSeparator); sb.Append("ZZ");              // ISA07 - Receiver Qualifier
        sb.Append(ElementSeparator); sb.Append("TXMCO01        "); // ISA08 - Receiver ID (15 chars)
        sb.Append(ElementSeparator); sb.Append(timestamp.ToString("yyMMdd")); // ISA09 - Date
        sb.Append(ElementSeparator); sb.Append(timestamp.ToString("HHmm"));  // ISA10 - Time
        sb.Append(ElementSeparator); sb.Append("^");               // ISA11 - Repetition Separator
        sb.Append(ElementSeparator); sb.Append("00501");           // ISA12 - Version
        sb.Append(ElementSeparator); sb.Append(controlNumber.PadLeft(9, '0')); // ISA13 - Control Number
        sb.Append(ElementSeparator); sb.Append("0");               // ISA14 - Ack Requested
        sb.Append(ElementSeparator); sb.Append("T");               // ISA15 - Usage Indicator (T=Test)
        sb.Append(ElementSeparator); sb.Append(SubElementSeparator); // ISA16 - Sub-element separator
        sb.Append(SegmentTerminator);
    }

    private static void WriteGs(StringBuilder sb, string controlNumber, DateTime timestamp)
    {
        sb.Append($"GS{ElementSeparator}HP{ElementSeparator}MCCBENCHMARK{ElementSeparator}TXMCO01" +
                  $"{ElementSeparator}{timestamp:yyyyMMdd}{ElementSeparator}{timestamp:HHmm}" +
                  $"{ElementSeparator}{controlNumber}{ElementSeparator}X{ElementSeparator}005010X220A1" +
                  $"{SegmentTerminator}");
    }

    private static int WriteMemberLoop(StringBuilder sb, SyntheticMember member, bool isSubscriber)
    {
        int segments = 0;

        // INS - Insured Benefit
        var benefitStatus = member.EnrollmentStatus == "Active" ? "A" :
                           member.EnrollmentStatus == "Terminated" ? "T" : "A";
        sb.Append($"INS{ElementSeparator}Y{ElementSeparator}18{ElementSeparator}" +
                  $"{member.MaintenanceTypeCode}{ElementSeparator}" +
                  $"{ElementSeparator}{ElementSeparator}{ElementSeparator}{ElementSeparator}" +
                  $"{benefitStatus}{SegmentTerminator}");
        segments++;

        // REF*0F - Subscriber Number
        sb.Append($"REF{ElementSeparator}0F{ElementSeparator}{member.SubscriberId}{SegmentTerminator}");
        segments++;

        // REF*1L - Group Number
        sb.Append($"REF{ElementSeparator}1L{ElementSeparator}{member.GroupNumber}{SegmentTerminator}");
        segments++;

        // DTP*336 - Employment Date
        if (member.EmploymentDate.HasValue)
        {
            sb.Append($"DTP{ElementSeparator}336{ElementSeparator}D8{ElementSeparator}" +
                      $"{member.EmploymentDate:yyyyMMdd}{SegmentTerminator}");
            segments++;
        }

        // NM1*IL - Member Name
        sb.Append($"NM1{ElementSeparator}IL{ElementSeparator}1{ElementSeparator}" +
                  $"{member.LastName}{ElementSeparator}{member.FirstName}{ElementSeparator}" +
                  $"{member.MiddleName ?? ""}{ElementSeparator}{ElementSeparator}{ElementSeparator}" +
                  $"34{ElementSeparator}{member.MemberId}{SegmentTerminator}");
        segments++;

        // N3 - Address
        sb.Append($"N3{ElementSeparator}{member.Address}{SegmentTerminator}");
        segments++;

        // N4 - City/State/Zip
        sb.Append($"N4{ElementSeparator}{member.City}{ElementSeparator}" +
                  $"{member.State}{ElementSeparator}{member.ZipCode}{SegmentTerminator}");
        segments++;

        // DMG - Demographics
        sb.Append($"DMG{ElementSeparator}D8{ElementSeparator}" +
                  $"{member.DateOfBirth:yyyyMMdd}{ElementSeparator}{member.Gender}{SegmentTerminator}");
        segments++;

        // Loop 2300 - Health Coverage for each coverage record
        foreach (var coverage in member.Coverages)
        {
            segments += WriteCoverageLoop(sb, coverage);
        }

        // Loop 2310 - PCP Assignment
        if (!string.IsNullOrEmpty(member.PcpNpi))
        {
            sb.Append($"LX{ElementSeparator}1{SegmentTerminator}");
            segments++;

            sb.Append($"NM1{ElementSeparator}P3{ElementSeparator}1{ElementSeparator}" +
                      $"{member.PcpName ?? "PCP"}{ElementSeparator}{ElementSeparator}{ElementSeparator}" +
                      $"{ElementSeparator}{ElementSeparator}XX{ElementSeparator}{member.PcpNpi}{SegmentTerminator}");
            segments++;
        }

        return segments;
    }

    private static int WriteDependentLoop(StringBuilder sb, SyntheticMember subscriber, SyntheticDependent dependent)
    {
        int segments = 0;

        // INS - Insured Benefit (dependent)
        var benefitStatus = dependent.EnrollmentStatus == "Active" ? "A" : "T";
        sb.Append($"INS{ElementSeparator}N{ElementSeparator}{dependent.RelationshipCode}" +
                  $"{ElementSeparator}021{ElementSeparator}{ElementSeparator}" +
                  $"{ElementSeparator}{ElementSeparator}{ElementSeparator}{benefitStatus}{SegmentTerminator}");
        segments++;

        // REF*0F - Subscriber Number (reference to subscriber)
        sb.Append($"REF{ElementSeparator}0F{ElementSeparator}{subscriber.SubscriberId}{SegmentTerminator}");
        segments++;

        // REF*1L - Group Number
        sb.Append($"REF{ElementSeparator}1L{ElementSeparator}{subscriber.GroupNumber}{SegmentTerminator}");
        segments++;

        // NM1*IL - Dependent Name
        sb.Append($"NM1{ElementSeparator}IL{ElementSeparator}1{ElementSeparator}" +
                  $"{dependent.LastName}{ElementSeparator}{dependent.FirstName}" +
                  $"{ElementSeparator}{ElementSeparator}{ElementSeparator}{ElementSeparator}" +
                  $"34{ElementSeparator}{dependent.MemberId}{SegmentTerminator}");
        segments++;

        // N3 - Address
        sb.Append($"N3{ElementSeparator}{dependent.Address}{SegmentTerminator}");
        segments++;

        // N4 - City/State/Zip
        sb.Append($"N4{ElementSeparator}{dependent.City}{ElementSeparator}" +
                  $"{dependent.State}{ElementSeparator}{dependent.ZipCode}{SegmentTerminator}");
        segments++;

        // DMG - Demographics
        sb.Append($"DMG{ElementSeparator}D8{ElementSeparator}" +
                  $"{dependent.DateOfBirth:yyyyMMdd}{ElementSeparator}{dependent.Gender}{SegmentTerminator}");
        segments++;

        // Loop 2300 - Health Coverage for each coverage record
        foreach (var coverage in dependent.Coverages)
        {
            segments += WriteCoverageLoop(sb, coverage);
        }

        return segments;
    }

    private static int WriteCoverageLoop(StringBuilder sb, SyntheticCoverage coverage)
    {
        int segments = 0;

        // HD - Health Coverage
        sb.Append($"HD{ElementSeparator}021{ElementSeparator}{ElementSeparator}" +
                  $"{coverage.InsuranceLineCode}{ElementSeparator}{ElementSeparator}" +
                  $"{coverage.CoverageLevelCode}{SegmentTerminator}");
        segments++;

        // DTP*348 - Benefit Begin Date
        sb.Append($"DTP{ElementSeparator}348{ElementSeparator}D8{ElementSeparator}" +
                  $"{coverage.EffectiveDate:yyyyMMdd}{SegmentTerminator}");
        segments++;

        // DTP*349 - Benefit End Date (if terminated)
        if (coverage.TermDate.HasValue)
        {
            sb.Append($"DTP{ElementSeparator}349{ElementSeparator}D8{ElementSeparator}" +
                      $"{coverage.TermDate:yyyyMMdd}{SegmentTerminator}");
            segments++;
        }

        return segments;
    }

    /// <summary>
    /// Perform basic structural validation on an 834 file content.
    /// </summary>
    /// <param name="content">Raw 834 file content.</param>
    /// <returns>True if basic structure is valid.</returns>
    public static bool ValidateStructure(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        // Must start with ISA
        if (!content.StartsWith("ISA"))
            return false;

        // Must contain required envelope segments
        var hasGS = content.Contains($"GS{ElementSeparator}");
        var hasST = content.Contains($"ST{ElementSeparator}834");
        var hasSE = content.Contains($"SE{ElementSeparator}");
        var hasGE = content.Contains($"GE{ElementSeparator}");
        var hasIEA = content.Contains($"IEA{ElementSeparator}");

        return hasGS && hasST && hasSE && hasGE && hasIEA;
    }
}
