using EnrollmentImportService.Models;

namespace EnrollmentImportService.Services.Edi;

public interface IEnrollment834EdiParser
{
    /// <summary>Parses raw X12 834 EDI text into the same <see cref="Enrollment834"/> shape the /import endpoint already accepts.</summary>
    Enrollment834 Parse(string ediContent, string fileName);
}

/// <summary>
/// Walks the 834 segment stream explicitly rather than relying on a
/// library's declarative loop-grouping: each subscriber is anchored by an
/// INS segment, and its own NM1/N3/N4/DMG only apply while <em>not</em>
/// inside a dependent's LS...LE block. A dependent's LS...LE-nested
/// NM1/N3/N4/DMG is instead routed into a fresh <see cref="Dependent"/> on
/// the current subscriber. Getting this distinction wrong — attributing a
/// dependent's demographics to the subscriber, or vice versa — is a
/// silent-data-corruption bug, not a crash, so the loop state is tracked
/// with an explicit "are we inside a dependent block" flag rather than a
/// single flat cursor.
/// </summary>
public sealed class Enrollment834EdiParser : IEnrollment834EdiParser
{
    public Enrollment834 Parse(string ediContent, string fileName)
    {
        var doc = X12Tokenizer.Tokenize(ediContent);

        var result = new Enrollment834
        {
            FileName = fileName,
            ParsedAt = DateTime.UtcNow
        };

        Sponsor? headerSponsor = null;
        MemberEnrollment? currentMember = null;
        Dependent? currentDependent = null;
        var insideDependentLoop = false;

        foreach (var seg in doc.Segments)
        {
            switch (seg.Id)
            {
                case "N1" when currentMember is null:
                    // Loop 1000A/1000B — sponsor/payer context at the header
                    // level, shared by every member loop that follows in
                    // this transaction (real files rarely vary this
                    // per-member; if a file ever does, a later N1 before
                    // any INS would simply overwrite headerSponsor).
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
                    // New subscriber loop starts. Flush whatever we were
                    // building (there's nothing on the very first INS).
                    FlushDependent(ref currentDependent, currentMember);
                    FlushMember(ref currentMember, result);

                    insideDependentLoop = false;
                    currentMember = new MemberEnrollment
                    {
                        Relationship = seg.Element(1) ?? string.Empty,
                        MaintenanceType = seg.Element(2) ?? string.Empty,
                        MaintenanceReason = seg.Element(3),
                        BenefitStatus = seg.Element(4) ?? string.Empty,
                        Sponsor = headerSponsor,
                        Demographics = new Demographics()
                    };
                    break;

                case "REF" when currentMember is not null && !insideDependentLoop:
                    ApplyRef(seg, currentMember);
                    break;

                case "DTP" when currentMember is not null && !insideDependentLoop:
                    ApplyDtp(seg, currentMember);
                    break;

                case "LS":
                    // Start of a dependent (Loop 2000/2100 under 2700).
                    insideDependentLoop = true;
                    currentDependent = new Dependent();
                    break;

                case "LE":
                    FlushDependent(ref currentDependent, currentMember);
                    insideDependentLoop = false;
                    break;

                case "NM1" when insideDependentLoop && currentDependent is not null:
                    ApplyNm1(seg, currentDependent);
                    break;

                case "NM1" when currentMember is not null:
                    ApplyNm1(seg, currentMember.Demographics!);
                    break;

                case "N3" when insideDependentLoop && currentDependent is not null:
                    ApplyN3(seg, currentDependent);
                    break;

                case "N3" when currentMember is not null:
                    ApplyN3(seg, currentMember.Demographics!);
                    break;

                case "N4" when insideDependentLoop && currentDependent is not null:
                    ApplyN4(seg, currentDependent);
                    break;

                case "N4" when currentMember is not null:
                    ApplyN4(seg, currentMember.Demographics!);
                    break;

                case "DMG" when insideDependentLoop && currentDependent is not null:
                    currentDependent.DateOfBirth = FormatDate(seg.Element(1));
                    currentDependent.Gender = seg.Element(2);
                    break;

                case "DMG" when currentMember is not null:
                    currentMember.Demographics!.DateOfBirth = FormatDate(seg.Element(1));
                    currentMember.Demographics.Gender = seg.Element(2);
                    break;

                case "HD" when insideDependentLoop && currentDependent is not null:
                    (currentDependent.Coverage ??= []).Add(BuildCoverage(seg));
                    break;

                case "HD" when currentMember is not null:
                    currentMember.Coverage.Add(BuildCoverage(seg));
                    break;
            }
        }

        FlushDependent(ref currentDependent, currentMember);
        FlushMember(ref currentMember, result);

        result.TransactionCount = result.Enrollments.Count;
        return result;
    }

    private static void FlushDependent(ref Dependent? dependent, MemberEnrollment? owner)
    {
        if (dependent is null)
        {
            return;
        }
        owner?.Dependents.Add(dependent);
        dependent = null;
    }

    private static void FlushMember(ref MemberEnrollment? member, Enrollment834 result)
    {
        if (member is null)
        {
            return;
        }
        result.Enrollments.Add(member);
        member = null;
    }

    private static void ApplyRef(X12Segment seg, MemberEnrollment member)
    {
        var qualifier = seg.Element(0);
        var value = seg.Element(1);
        switch (qualifier)
        {
            case "0F": member.SubscriberId = value; break;
            case "1L": member.GroupNumber = value; break;
            case "ZZ": member.EmployeeId = value; break;
        }
    }

    private static void ApplyDtp(X12Segment seg, MemberEnrollment member)
    {
        var qualifier = seg.Element(0);
        var date = FormatDate(seg.Element(2));
        switch (qualifier)
        {
            case "303": member.EnrollmentDate = date; break;
            case "356": member.TerminationDate = date; break;
            case "336": member.EmploymentStartDate = date; break;
        }
    }

    private static void ApplyNm1(X12Segment seg, Demographics target)
    {
        target.EntityType = seg.Element(1);
        target.LastName = seg.Element(2) ?? string.Empty;
        target.FirstName = seg.Element(3) ?? string.Empty;
        target.MiddleName = seg.Element(4);
        target.Suffix = seg.Element(5);
        target.IdQualifier = seg.Element(7);
        target.Id = seg.Element(8);
    }

    private static void ApplyNm1(X12Segment seg, Dependent target)
    {
        target.EntityType = seg.Element(1);
        target.LastName = seg.Element(2) ?? string.Empty;
        target.FirstName = seg.Element(3) ?? string.Empty;
        target.MiddleName = seg.Element(4);
        target.Suffix = seg.Element(5);
        target.IdQualifier = seg.Element(7);
        target.Id = seg.Element(8);
    }

    private static void ApplyN3(X12Segment seg, Demographics target)
    {
        target.Address1 = seg.Element(0);
        target.Address2 = seg.Element(1);
    }

    private static void ApplyN3(X12Segment seg, Dependent target)
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

    private static void ApplyN4(X12Segment seg, Dependent target)
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
