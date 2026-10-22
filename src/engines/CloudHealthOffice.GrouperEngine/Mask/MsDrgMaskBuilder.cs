using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Models;

namespace CloudHealthOffice.GrouperEngine.Mask;

/// <summary>
/// Turns a stay into the mask its formulas are evaluated against.
///
/// <para>
/// This is the step that decides what a claim <i>means</i> to the grouping
/// logic. Every code on the claim contributes its operand lists at the
/// position it appeared — the principal diagnosis at <c>PDX:</c>, each
/// secondary at <c>SDX:</c>, each procedure unprefixed — and the claim as a
/// whole contributes a handful of operands that no single code can:
/// whether the patient lived, where they were discharged to, and the edits
/// that make a claim ungroupable.
/// </para>
///
/// <para>
/// It is a port of CMS's own mask builder, kept deliberately close to it. The
/// odd-looking parts are all CMS's and all load-bearing; each is commented
/// where it appears. What is <i>not</i> here yet is listed under
/// <see cref="NotYetBuilt"/>, because a mask that silently lacks an operand
/// produces a wrong DRG rather than an error.
/// </para>
/// </summary>
public sealed class MsDrgMaskBuilder
{
    /// <summary>
    /// Operands this builder does not yet contribute, and so formulas that
    /// cannot yet be trusted. Empty, and kept so that the next gap is declared
    /// here rather than left to be discovered: a mask that silently lacks an
    /// operand produces a wrong DRG, not an error.
    /// </summary>
    public static IReadOnlyList<string> NotYetBuilt { get; } = [];

    /// <summary>
    /// Discharge statuses that mean a transfer to another acute setting, which
    /// CMS marks with a single operand rather than by status. The set is
    /// CMS's: short-term hospital (2), cancer or children's hospital (5),
    /// critical access hospital (66), and the planned-readmission counterparts
    /// of each (82, 85, 94).
    /// </summary>
    private static readonly HashSet<int> TransferToAcute = [2, 5, 66, 82, 85, 94];

    /// <summary>
    /// The two operand lists that make a procedure an operating-room
    /// procedure. CMS derives <c>ORPROC</c> from membership of either, rather
    /// than storing it.
    /// </summary>
    private const string OperatingRoomListOne = "d477";
    private const string OperatingRoomListTwo = "d468";

    private const string OperatingRoomProcedure = "ORPROC";
    private const string Incident = "incident";
    private const string AnyDiagnosisPresent = "ANYDX";
    private const string Alive = "ALIVE";
    private const string TransferToAcuteOperand = "XFRNBA";
    private const string InvalidPrincipalDiagnosis = "invalid_pdx";
    private const string PrincipalDiagnosisIsExternalCause = "pdx_ecode";
    // The MCE edit operands are lower case here and upper case in the
    // formulas. That is not a typo on either side: CMS resolves the formula's
    // spelling case-insensitively and then looks for the tables' spelling, so
    // the mask has to hold the tables' one. Verified against CMS's own
    // grouper, which answers false for an upper-case mask entry.
    private const string InvalidDischargeStatus = "invalid_dstat";
    private const string InvalidSex = "invalid_sex";
    private const string LeftAgainstMedicalAdvice = "AMA";
    private const string Died = "DIED";

    /// <summary>
    /// The operands this builder writes itself rather than reading off a code
    /// table, each at the position it writes them.
    ///
    /// <para>
    /// Published because it is the rest of the mask's vocabulary: an operand a
    /// formula names is satisfiable only if some code carries it or it is one
    /// of these, and that is how a row can be shown to be unreachable rather
    /// than merely unmatched on one claim.
    /// </para>
    ///
    /// <para>
    /// <c>invalid_age</c> is deliberately absent. It is one of CMS's five MCE
    /// edit operands, but no V44 formula names it and CMS's own mask builder
    /// writes no age operand either, so there is nothing for it to do.
    /// </para>
    /// </summary>
    public static IReadOnlyList<OperandName> DerivedOperands { get; } =
    [
        new(OperandPosition.Unqualified, OperatingRoomProcedure),
        new(OperandPosition.Only, Incident),
        new(OperandPosition.Unqualified, AnyDiagnosisPresent),
        new(OperandPosition.Unqualified, Alive),
        new(OperandPosition.Unqualified, TransferToAcuteOperand),
        new(OperandPosition.Unqualified, LeftAgainstMedicalAdvice),
        new(OperandPosition.Unqualified, Died),
        new(OperandPosition.PrincipalDiagnosis, InvalidPrincipalDiagnosis),
        new(OperandPosition.PrincipalDiagnosis, PrincipalDiagnosisIsExternalCause),
        new(OperandPosition.Unqualified, InvalidDischargeStatus),
        new(OperandPosition.Unqualified, InvalidSex),
    ];

    /// <summary>CMS's sentinel for a sex it cannot read.</summary>
    private const int UnknownSex = -1;

    private readonly IMsDrgCodeAttributeSource codes;
    private readonly IMsDrgClusterSource clusters;

    /// <summary>Builds masks from the given CMS code tables.</summary>
    /// <param name="codes">The code-to-operand tables.</param>
    /// <param name="clusters">
    /// The cluster definitions. Omit for a table set that has none; a stay with
    /// a qualifying <i>combination</i> of procedures rather than a single
    /// qualifying procedure is then under-coded, which is why the production
    /// path passes them.
    /// </param>
    public MsDrgMaskBuilder(
        IMsDrgCodeAttributeSource codes, IMsDrgClusterSource? clusters = null)
    {
        ArgumentNullException.ThrowIfNull(codes);
        this.codes = codes;
        this.clusters = clusters ?? IMsDrgClusterSource.None;
    }

    /// <summary>
    /// Builds the mask for a stay, and reports the codes the tables did not
    /// recognise.
    /// </summary>
    /// <param name="request">The stay.</param>
    /// <param name="unrecognisedCodes">
    /// Codes absent from the tables, in the order they appeared. A caller must
    /// treat a non-empty list as a reason to refuse: an unrecognised code is
    /// not the same as an absent one, and guessing is how a grouper quietly
    /// under-codes a stay.
    /// </param>
    public AttributeMask Build(
        MsDrgGroupingRequest request, out IReadOnlyList<string> unrecognisedCodes)
    {
        var built = BuildClaim(request);
        unrecognisedCodes = built.UnrecognisedCodes;
        return built.Mask;
    }

    /// <summary>
    /// Builds the mask and the claim's codes together.
    ///
    /// <para>
    /// The codes are needed as well as the mask because eleven V44 operands do
    /// not read the mask at all: they walk the claim's diagnoses or procedures
    /// and count. Building both here keeps the two in step — the codes are the
    /// same ones, in the same order, that contributed the mask.
    /// </para>
    /// </summary>
    /// <param name="request">The stay.</param>
    public MsDrgMaskedClaim BuildClaim(MsDrgGroupingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var unrecognised = new List<string>();
        var mask = new AttributeMask();

        // CMS's own evaluation context holds the principal diagnosis first and
        // then the secondaries, and the procedures exactly as submitted. A
        // satisfied cluster's pseudo-code is NOT among them: a cluster reaches
        // the formulas through the mask only. MDC_20_FALL_THRU reads
        // diagnoses[0] and nothing else, so the order is not cosmetic.
        var claimDiagnoses = new List<ClaimDiagnosis>();
        var claimProcedures = new List<ClaimProcedure>();

        // CMS builds the secondaries first and the principal last. The order is
        // visible in a trace and in the audit record, so it is kept.
        foreach (var secondary in request.SecondaryDiagnoses)
        {
            if (!codes.TryGetDiagnosis(secondary.Code, out var attributes))
            {
                unrecognised.Add(secondary.Code);
                claimDiagnoses.Add(Unrecognised(secondary.Code));
                continue;
            }

            AddDiagnosis(mask, attributes, OperandPosition.SecondaryDiagnosis);
            claimDiagnoses.Add(ClaimDiagnosis.Create(
                secondary.Code, attributes.Mdc, attributes.Severity, attributes.Operands));
        }

        // Clusters are resolved before the procedures are added, because a
        // satisfied cluster decides which of those procedures' operands stop
        // counting.
        var procedureCodes = request.Procedures.Select(procedure => procedure.Code).ToList();
        var satisfied = MsDrgClusterResolver.Resolve(clusters, procedureCodes);
        var suppressionByCode = SuppressionByCode(satisfied);

        foreach (var procedure in request.Procedures)
        {
            if (!codes.TryGetProcedure(procedure.Code, out var attributes))
            {
                unrecognised.Add(procedure.Code);
                claimProcedures.Add(ClaimProcedure.Create(procedure.Code, []));
                continue;
            }

            suppressionByCode.TryGetValue(procedure.Code, out var suppressed);
            AddProcedure(mask, attributes, suppressed);

            // The counting operands read the code's own attributes, which
            // cluster suppression does not touch: suppression removes an
            // operand from the mask, and these operands never consult it.
            claimProcedures.Add(ClaimProcedure.Create(procedure.Code, attributes.Operands));
        }

        // A satisfied cluster's own code is looked up in the procedure table
        // like any other procedure, because that is what it now is to the rest
        // of the grouper. Its operands are never suppressed -- only the
        // individual codes' are, which is the whole point of the mechanism.
        foreach (var cluster in satisfied)
        {
            if (codes.TryGetProcedure(cluster.Cluster.ClusterCode, out var attributes))
            {
                AddProcedure(mask, attributes, suppressedMdcs: 0);
            }
        }

        var principalRecognised =
            codes.TryGetDiagnosis(request.PrincipalDiagnosis.Code, out var principal);

        if (principalRecognised)
        {
            AddDiagnosis(mask, principal, OperandPosition.PrincipalDiagnosis);
            claimDiagnoses.Insert(0, ClaimDiagnosis.Create(
                request.PrincipalDiagnosis.Code, principal.Mdc,
                principal.Severity, principal.Operands));
        }
        else
        {
            unrecognised.Add(request.PrincipalDiagnosis.Code);
            claimDiagnoses.Insert(0, Unrecognised(request.PrincipalDiagnosis.Code));

            // An unrecognised principal diagnosis is itself an operand: the
            // formulas for the ungroupable DRGs test for it. So the mask is
            // still well formed, and the grouper can reach 998 rather than
            // crash.
            mask.Add(new OperandName(
                OperandPosition.PrincipalDiagnosis, InvalidPrincipalDiagnosis));
        }

        // An external-cause code as principal diagnosis. CMS tests the first
        // character only, and tests it case-insensitively.
        if (StartsWithExternalCauseLetter(request.PrincipalDiagnosis.Code))
        {
            mask.Add(new OperandName(
                OperandPosition.PrincipalDiagnosis, PrincipalDiagnosisIsExternalCause));
        }

        AddDischargeStatus(mask, request.DischargeStatus);

        // Always present, so that a formula can ask "is there a diagnosis at
        // all" without naming one.
        mask.Add(new OperandName(OperandPosition.Unqualified, AnyDiagnosisPresent));

        AddOnlyIncident(mask, request, principalRecognised ? principal : null);
        AddSexEdit(mask, request, principalRecognised ? principal : null);

        return new MsDrgMaskedClaim(
            mask,
            new ClaimCodes(claimDiagnoses, claimProcedures),
            unrecognised);
    }

    /// <summary>
    /// A code the tables do not know, kept in the claim's code list so that
    /// positions do not shift under the operands that read them.
    ///
    /// <para>
    /// Its MDC is -1, which no formula asks for. That is safe only because an
    /// unrecognised code is a reason to refuse the claim, reported in
    /// <see cref="MsDrgMaskedClaim.UnrecognisedCodes"/>; it is not a default
    /// that should ever decide a DRG.
    /// </para>
    /// </summary>
    private static ClaimDiagnosis Unrecognised(string code) =>
        ClaimDiagnosis.Create(code, -1, ComplicationLevel.None, []);

    /// <summary>
    /// Adds a procedure's operands, suppressed in the MDCs a cluster has
    /// claimed them for, plus <c>ORPROC</c> where the procedure earns it.
    /// </summary>
    private static void AddProcedure(
        AttributeMask mask, ProcedureCodeAttributes attributes, int suppressedMdcs)
    {
        foreach (var operand in attributes.Operands)
        {
            mask.Add(new MaskAttribute(
                new OperandName(OperandPosition.Unqualified, operand), suppressedMdcs));
        }

        // ORPROC is not in the tables: CMS derives it from the procedure's own
        // lists, once per qualifying procedure, and it carries the same
        // suppression as the operands it was derived from.
        if (attributes.Operands.Contains(OperatingRoomListOne)
            || attributes.Operands.Contains(OperatingRoomListTwo))
        {
            mask.Add(new MaskAttribute(
                new OperandName(OperandPosition.Unqualified, OperatingRoomProcedure),
                suppressedMdcs));
        }
    }

    /// <summary>
    /// The MDCs each contributing procedure code is suppressed in. A code that
    /// contributed to two clusters is suppressed in the union of their
    /// restrictions.
    /// </summary>
    private static Dictionary<string, int> SuppressionByCode(
        IReadOnlyList<SatisfiedCluster> satisfied)
    {
        var suppression = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var cluster in satisfied)
        {
            var restricted = MaskAttribute.MdcMask(cluster.Cluster.RestrictedMdcs);

            foreach (var code in cluster.ContributingCodes)
            {
                suppression[code] = suppression.TryGetValue(code, out var already)
                    ? already | restricted
                    : restricted;
            }
        }

        return suppression;
    }

    private static void AddDiagnosis(
        AttributeMask mask, DiagnosisCodeAttributes attributes, OperandPosition position)
    {
        // All three lists enter at the same position. CMS reads them through
        // separate accessors that return the same rows for a principal and a
        // secondary diagnosis, differing only in the prefix they attach.
        foreach (var operand in attributes.Operands)
        {
            mask.Add(new OperandName(position, operand));
        }

        foreach (var operand in attributes.DiagnosisCategoryOperands)
        {
            mask.Add(new OperandName(position, operand));
        }

        foreach (var operand in attributes.HacOperands)
        {
            mask.Add(new OperandName(position, operand));
        }
    }

    private static bool StartsWithExternalCauseLetter(string code) =>
        code.Length > 0 && char.ToUpperInvariant(code[0]) is 'V' or 'W' or 'X' or 'Y';

    private static void AddDischargeStatus(AttributeMask mask, string? dischargeStatus)
    {
        var status = ParseDischargeStatus(dischargeStatus);

        // ALIVE is the absence of DIED, not a status of its own — an
        // unrecognised status counts as alive, which is CMS's behaviour and
        // not an accident: a stay with a garbled status is not a death.
        if (status != 20)
        {
            mask.Add(new OperandName(OperandPosition.Unqualified, Alive));
        }

        // Only three statuses have an operand of their own.
        var operand = status switch
        {
            -1 => InvalidDischargeStatus,
            7 => LeftAgainstMedicalAdvice,
            20 => Died,
            _ => null,
        };

        if (operand is not null)
        {
            mask.Add(new OperandName(OperandPosition.Unqualified, operand));
        }

        if (TransferToAcute.Contains(status))
        {
            mask.Add(new OperandName(OperandPosition.Unqualified, TransferToAcuteOperand));
        }
    }

    /// <summary>
    /// The UB-04 discharge statuses CMS recognises. A status outside this set
    /// is not a status CMS has no operand for — it is an invalid status, which
    /// is a different thing and has an operand of its own. The set is sparse
    /// because UB-04's is: there is no 08, no 22, no 31.
    /// </summary>
    private static readonly HashSet<int> KnownDischargeStatuses =
    [
        1, 2, 3, 4, 5, 6, 7,
        20, 21, 30, 43, 50, 51,
        61, 62, 63, 64, 65, 66, 69, 70,
        81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95,
    ];

    /// <summary>
    /// UB-04 discharge status as an integer, or -1 for anything CMS does not
    /// recognise — which is itself a formula operand.
    /// </summary>
    private static int ParseDischargeStatus(string? dischargeStatus) =>
        int.TryParse(dischargeStatus, out var parsed)
        && KnownDischargeStatuses.Contains(parsed)
            ? parsed
            : -1;

    /// <summary>
    /// <c>ONLY:incident</c> — satisfied when the principal diagnosis is on the
    /// incident list <i>and every valid secondary is too</i>. This is the one
    /// operand whose meaning is "all of them" rather than "at least one", and
    /// it is why the <c>ONLY:</c> prefix exists. An unrecognised secondary does
    /// not break the run, exactly as in CMS: the test is over valid codes.
    /// </summary>
    private void AddOnlyIncident(
        AttributeMask mask, MsDrgGroupingRequest request, DiagnosisCodeAttributes? principal)
    {
        if (principal is null || !HasOperand(principal, Incident))
        {
            return;
        }

        foreach (var secondary in request.SecondaryDiagnoses)
        {
            if (!codes.TryGetDiagnosis(secondary.Code, out var attributes))
            {
                continue;
            }

            if (!HasOperand(attributes, Incident))
            {
                return;
            }
        }

        mask.Add(new OperandName(OperandPosition.Only, Incident));
    }

    /// <summary>
    /// <c>INVALID_SEX</c> — the MCE sex edit. CMS raises it only when the sex
    /// is unknown <i>and</i> the principal diagnosis is sex-specific. It enters
    /// the mask unprefixed, which is why a formula tests it without a prefix.
    ///
    /// <para>
    /// Note what this does <i>not</i> cover, because it reads like a bug and is
    /// not one. A stay that states a sex which the code does not allow — a male
    /// stay with a female-only principal diagnosis — is a sex conflict in CMS
    /// too, but it does not raise this operand. CMS instead falls the MDC back
    /// to the code's female entry and groups the stay. Only an <i>unknown</i>
    /// sex makes the claim ungroupable, which is the distinction between a
    /// claim that contradicts itself and one that is merely incomplete.
    /// </para>
    /// </summary>
    private static void AddSexEdit(
        AttributeMask mask, MsDrgGroupingRequest request, DiagnosisCodeAttributes? principal)
    {
        if (principal?.SexMdcs is null)
        {
            return;
        }

        if (ParseSex(request.Sex) == UnknownSex)
        {
            mask.Add(new OperandName(OperandPosition.Unqualified, InvalidSex));
        }
    }

    /// <summary>
    /// UB-04 sex as CMS's digit — 1 male, 2 female — or
    /// <see cref="UnknownSex"/> for anything else, blanks and unknowns
    /// included.
    /// </summary>
    private static int ParseSex(string? sex) => sex?.Trim().ToUpperInvariant() switch
    {
        "1" or "M" or "MALE" => 1,
        "2" or "F" or "FEMALE" => 2,
        _ => UnknownSex,
    };

    private static bool HasOperand(DiagnosisCodeAttributes attributes, string operand) =>
        attributes.Operands.Contains(operand, StringComparer.OrdinalIgnoreCase)
        || attributes.DiagnosisCategoryOperands.Contains(operand, StringComparer.OrdinalIgnoreCase);
}
