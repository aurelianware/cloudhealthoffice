using EnrollmentImportService.Models;

namespace EnrollmentImportService.Services.Edi;

public interface IEnrollment834EdiParser
{
    /// <summary>Parses raw X12 834 EDI text into the same <see cref="Enrollment834"/> shape the /import endpoint already accepts.</summary>
    Enrollment834 Parse(string ediContent, string fileName);
}

/// <summary>
/// Walks the 834 segment stream explicitly, following the 005010X220A1 loop
/// structure rather than relying on a library's declarative loop-grouping:
///
/// <list type="bullet">
/// <item>Every member — subscriber <em>or</em> dependent — is its own Loop
/// 2000, anchored by an INS segment. INS01 says which (Y=subscriber,
/// N=dependent) and INS02 carries the individual relationship code.</item>
/// <item>A dependent's Loop 2000 is tied to its subscriber by REF*0F
/// (subscriber identifier), not by position. Dependents are attached to the
/// subscriber in the same ST/SE whose REF*0F matches; a dependent whose
/// subscriber isn't in the transaction set (e.g. a newborn add sent alone)
/// lands in <see cref="Enrollment834.DependentEnrollments"/>.</item>
/// <item>Only Loop 2100A (NM1*IL / NM1*74) feeds member demographics. The
/// other 2100 loops (incorrect name, mailing address, employer, custodial
/// parent, ...) and the 2310 provider / 2320 COB loops reuse NM1/N3/N4/DMG,
/// so those segments are routed by the loop they're in, never applied
/// blindly to the member.</item>
/// <item>Loop 2300 starts at HD; its DTP*348/349 are that coverage's own
/// benefit begin/end dates.</item>
/// <item>LS...LE wraps Loop 2700 (member reporting categories) — not a
/// dependent. Its N1/REF/DTP are captured as reporting categories and
/// never touch member-level fields.</item>
/// </list>
///
/// Mis-attributing one member's data to another is a silent-data-corruption
/// bug, not a crash, which is why loop state is tracked explicitly.
/// </summary>
public sealed class Enrollment834EdiParser : IEnrollment834EdiParser
{
    private enum Loop
    {
        /// <summary>Outside any member loop (header 1000A/B/C, or between ST/SE).</summary>
        Header,
        /// <summary>Loop 2000 member level detail (INS/REF/DTP).</summary>
        Member,
        /// <summary>Loop 2100A member name — feeds demographics.</summary>
        MemberName,
        /// <summary>Loops 2100B-2100H — other names/addresses; ignored.</summary>
        OtherMemberEntity,
        /// <summary>Loop 2300 health coverage (HD/DTP/AMT/REF/IDC).</summary>
        Coverage,
        /// <summary>Loops 2310/2320/2330 under a coverage — provider/COB; ignored.</summary>
        CoverageSubLoop,
        /// <summary>Loop 2700/2750 member reporting categories (between LS and LE).</summary>
        ReportingCategories,
        /// <summary>After LE: nothing member-level may follow until the next INS/SE.</summary>
        AfterReportingCategories
    }

    /// <summary>One Loop 2000 under construction. The INS/REF/DTP/2100A/2300 content is identical for subscribers and dependents, so both are built into a <see cref="MemberEnrollment"/> and projected on flush.</summary>
    private sealed class MemberLoop
    {
        public required bool IsSubscriber { get; init; }
        public required MemberEnrollment Record { get; init; }
        public string? MemberIdentifier { get; set; }
        public CoverageDetail? CurrentCoverage { get; set; }
        public ReportingCategory? CurrentCategory { get; set; }
    }

    public Enrollment834 Parse(string ediContent, string fileName)
    {
        var doc = X12Tokenizer.Tokenize(ediContent);

        var result = new Enrollment834
        {
            FileName = fileName,
            ParsedAt = DateTime.UtcNow
        };

        Sponsor? headerSponsor = null;
        MemberLoop? current = null;
        var loop = Loop.Header;

        // Subscribers seen so far in the current ST/SE, keyed by REF*0F, so
        // dependents can be attached by identifier rather than by position.
        var subscribersById = new Dictionary<string, MemberEnrollment>(StringComparer.Ordinal);
        MemberEnrollment? lastSubscriber = null;

        foreach (var seg in doc.Segments)
        {
            switch (seg.Id)
            {
                case "ST":
                    // Each transaction set is self-contained: header sponsor
                    // and subscriber lookup do not carry across ST/SE.
                    FlushMember(ref current, result, subscribersById, ref lastSubscriber);
                    subscribersById.Clear();
                    lastSubscriber = null;
                    headerSponsor = null;
                    loop = Loop.Header;
                    break;

                case "SE":
                    FlushMember(ref current, result, subscribersById, ref lastSubscriber);
                    loop = Loop.Header;
                    break;

                case "N1" when loop == Loop.Header:
                    // Loop 1000A/1000B — sponsor/payer context at the header
                    // level, shared by every member loop that follows in
                    // this transaction set.
                    if (seg.Element(0) == "P5")
                    {
                        headerSponsor = new Sponsor
                        {
                            Qualifier = "P5",
                            Name = seg.Element(1) ?? string.Empty,
                            IdQualifier = seg.Element(2),
                            Id = seg.Element(3)
                        };
                    }
                    break;

                case "INS":
                    // New Loop 2000 — subscriber (INS01=Y) or dependent (N).
                    FlushMember(ref current, result, subscribersById, ref lastSubscriber);

                    current = new MemberLoop
                    {
                        IsSubscriber = !string.Equals(seg.Element(0), "N", StringComparison.Ordinal),
                        Record = new MemberEnrollment
                        {
                            Relationship = seg.Element(1) ?? string.Empty,
                            MaintenanceType = seg.Element(2) ?? string.Empty,
                            MaintenanceReason = seg.Element(3),
                            BenefitStatus = seg.Element(4) ?? string.Empty,
                            Sponsor = headerSponsor,
                            Demographics = new Demographics()
                        }
                    };
                    loop = Loop.Member;
                    break;

                // No 2100 loop defines REF/DTP, so a REF/DTP seen while in
                // 2000/2100 is member-level regardless of exact position.
                case "REF" when current is not null && IsMemberLevel(loop):
                    ApplyRef(seg, current);
                    break;

                case "REF" when current?.CurrentCategory is not null && loop == Loop.ReportingCategories:
                    current.CurrentCategory.ReferenceQualifier = seg.Element(0);
                    current.CurrentCategory.ReferenceValue = seg.Element(1);
                    break;

                case "DTP" when current is not null && IsMemberLevel(loop):
                    ApplyDtp(seg, current.Record);
                    break;

                case "DTP" when current?.CurrentCoverage is not null && loop == Loop.Coverage:
                    ApplyCoverageDtp(seg, current.CurrentCoverage);
                    break;

                case "DTP" when current?.CurrentCategory is not null && loop == Loop.ReportingCategories:
                    current.CurrentCategory.Date = FormatDate(seg.Element(2));
                    break;

                case "NM1" when current is not null && IsMemberLevel(loop):
                    // 2100A is NM1*IL (or NM1*74 for a corrected name);
                    // every other NM101 opens a 2100B-H loop we don't map.
                    if (seg.Element(0) is "IL" or "74")
                    {
                        ApplyNm1(seg, current.Record.Demographics!);
                        loop = Loop.MemberName;
                    }
                    else
                    {
                        loop = Loop.OtherMemberEntity;
                    }
                    break;

                case "NM1" when loop is Loop.Coverage or Loop.CoverageSubLoop:
                    // 2310 provider / 2330 COB related entity.
                    loop = Loop.CoverageSubLoop;
                    break;

                case "N3" when current is not null && loop == Loop.MemberName:
                    ApplyN3(seg, current.Record.Demographics!);
                    break;

                case "N4" when current is not null && loop == Loop.MemberName:
                    ApplyN4(seg, current.Record.Demographics!);
                    break;

                case "DMG" when current is not null && loop == Loop.MemberName:
                    current.Record.Demographics!.DateOfBirth = FormatDate(seg.Element(1));
                    current.Record.Demographics.Gender = seg.Element(2);
                    break;

                case "HD" when current is not null:
                    // Loop 2300 — one per coverage line.
                    current.CurrentCoverage = BuildCoverage(seg);
                    current.Record.Coverage.Add(current.CurrentCoverage);
                    loop = Loop.Coverage;
                    break;

                case "LX" when current is not null && loop == Loop.ReportingCategories:
                    // Loop 2750 — one reporting category.
                    current.CurrentCategory = new ReportingCategory();
                    (current.Record.ReportingCategories ??= []).Add(current.CurrentCategory);
                    break;

                case "N1" when current?.CurrentCategory is not null && loop == Loop.ReportingCategories:
                    current.CurrentCategory.Name = seg.Element(1);
                    break;

                case "LX" when loop is Loop.Coverage or Loop.CoverageSubLoop:
                case "COB" when loop is Loop.Coverage or Loop.CoverageSubLoop:
                    // 2310 provider / 2320 COB start.
                    loop = Loop.CoverageSubLoop;
                    break;

                case "LS" when current is not null:
                    // Loop 2700 member reporting categories — NOT a dependent.
                    loop = Loop.ReportingCategories;
                    break;

                case "LE" when current is not null:
                    current.CurrentCategory = null;
                    loop = Loop.AfterReportingCategories;
                    break;
            }
        }

        FlushMember(ref current, result, subscribersById, ref lastSubscriber);

        result.TransactionCount = result.Enrollments.Count + result.DependentEnrollments.Count;
        return result;
    }

    private static bool IsMemberLevel(Loop loop) =>
        loop is Loop.Member or Loop.MemberName or Loop.OtherMemberEntity;

    private static void FlushMember(
        ref MemberLoop? member,
        Enrollment834 result,
        Dictionary<string, MemberEnrollment> subscribersById,
        ref MemberEnrollment? lastSubscriber)
    {
        if (member is null)
        {
            return;
        }

        var record = member.Record;

        // X220A1 terminations normally carry the end date per coverage (2300
        // DTP*349) rather than a member-level DTP*357; surface the latest one
        // as the member's termination date so 024 isn't left dateless.
        if (string.IsNullOrEmpty(record.TerminationDate) && record.MaintenanceType == "024")
        {
            record.TerminationDate = record.Coverage
                .Select(c => c.BenefitEndDate)
                .Where(d => !string.IsNullOrEmpty(d))
                .Max(StringComparer.Ordinal);
        }

        if (member.IsSubscriber)
        {
            result.Enrollments.Add(record);
            if (!string.IsNullOrEmpty(record.SubscriberId))
            {
                subscribersById[record.SubscriberId] = record;
            }
            lastSubscriber = record;
        }
        else
        {
            // REF*0F is required on every X220A1 member loop. Fall back to the
            // most recent subscriber only when a non-compliant file omits it.
            MemberEnrollment? owner = null;
            if (!string.IsNullOrEmpty(record.SubscriberId))
            {
                subscribersById.TryGetValue(record.SubscriberId, out owner);
            }
            else
            {
                owner = lastSubscriber;
            }

            var dependent = ToDependent(member, owner);
            if (owner is not null)
            {
                owner.Dependents.Add(dependent);
            }
            else
            {
                result.DependentEnrollments.Add(dependent);
            }
        }

        member = null;
    }

    private static Dependent ToDependent(MemberLoop member, MemberEnrollment? owner)
    {
        var r = member.Record;
        var d = r.Demographics!;
        return new Dependent
        {
            Relationship = string.IsNullOrEmpty(r.Relationship) ? null : r.Relationship,
            MaintenanceType = string.IsNullOrEmpty(r.MaintenanceType) ? null : r.MaintenanceType,
            MaintenanceReason = r.MaintenanceReason,
            BenefitStatus = string.IsNullOrEmpty(r.BenefitStatus) ? null : r.BenefitStatus,
            SubscriberId = r.SubscriberId ?? owner?.SubscriberId,
            MemberIdentifier = member.MemberIdentifier,
            GroupNumber = r.GroupNumber ?? owner?.GroupNumber,
            EnrollmentDate = r.EnrollmentDate,
            TerminationDate = r.TerminationDate,
            EntityType = d.EntityType,
            LastName = d.LastName,
            FirstName = d.FirstName,
            MiddleName = d.MiddleName,
            Suffix = d.Suffix,
            IdQualifier = d.IdQualifier,
            Id = d.Id,
            Address1 = d.Address1,
            Address2 = d.Address2,
            City = d.City,
            State = d.State,
            Zip = d.Zip,
            DateOfBirth = d.DateOfBirth,
            Gender = d.Gender,
            Coverage = r.Coverage.Count > 0 ? r.Coverage : null,
            ReportingCategories = r.ReportingCategories
        };
    }

    private static void ApplyRef(X12Segment seg, MemberLoop member)
    {
        var qualifier = seg.Element(0);
        var value = seg.Element(1);
        switch (qualifier)
        {
            case "0F": member.Record.SubscriberId = value; break;
            case "1L": member.Record.GroupNumber = value; break;
            case "ZZ": member.Record.EmployeeId = value; break;
            // Member supplemental identifier 23 (client number) is the
            // member-level key. Not 17 (client reporting category): that is
            // a category, often shared by every member of a family.
            case "23": member.MemberIdentifier = value; break;
        }
    }

    private static void ApplyDtp(X12Segment seg, MemberEnrollment member)
    {
        var qualifier = seg.Element(0);
        var date = FormatDate(seg.Element(2));
        switch (qualifier)
        {
            case "303": member.EnrollmentDate = date; break;       // maintenance effective
            case "356": member.EligibilityBeginDate = date; break; // eligibility begin
            case "357": member.TerminationDate = date; break;      // eligibility end
            case "336": member.EmploymentStartDate = date; break;  // employment begin
        }
    }

    private static void ApplyCoverageDtp(X12Segment seg, CoverageDetail coverage)
    {
        var qualifier = seg.Element(0);
        var date = FormatDate(seg.Element(2));
        switch (qualifier)
        {
            case "348": coverage.BenefitBeginDate = date; break;
            case "349": coverage.BenefitEndDate = date; break;
        }
    }

    private static void ApplyNm1(X12Segment seg, Demographics target)
    {
        target.EntityType = seg.Element(1);
        target.LastName = seg.Element(2) ?? string.Empty;
        target.FirstName = seg.Element(3) ?? string.Empty;
        target.MiddleName = seg.Element(4);
        target.Suffix = seg.Element(6);
        target.IdQualifier = seg.Element(7);
        target.Id = seg.Element(8);
    }

    private static void ApplyN3(X12Segment seg, Demographics target)
    {
        target.Address1 = seg.Element(0);
        target.Address2 = seg.Element(1);
    }

    private static void ApplyN4(X12Segment seg, Demographics target)
    {
        target.City = seg.Element(0);
        target.State = seg.Element(1);
        target.Zip = seg.Element(2);
    }

    private static CoverageDetail BuildCoverage(X12Segment seg) => new()
    {
        MaintenanceType = seg.Element(0),
        InsuranceLineCode = seg.Element(2) ?? string.Empty,
        PlanCoverageDescription = seg.Element(3),
        CoverageLevel = seg.Element(4)
    };

    /// <summary>834 dates are CCYYMMDD (D8 qualifier) — pass through as-is; EnrollmentImportService.ParseDate handles the string-&gt;DateTime conversion downstream.</summary>
    private static string? FormatDate(string? d8Date) => d8Date;
}
