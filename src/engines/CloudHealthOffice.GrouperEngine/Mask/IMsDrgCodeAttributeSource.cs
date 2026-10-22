using CloudHealthOffice.GrouperEngine.Domain;

namespace CloudHealthOffice.GrouperEngine.Mask;

/// <summary>
/// What CMS's tables say about one diagnosis code, for one grouper version.
///
/// <para>
/// The three operand lists are separate because CMS keeps them separate: a
/// diagnosis row carries up to three pattern numbers, and each resolves to its
/// own operand list. All three go into the mask at the same position, so the
/// split matters only for tracing which table a match came from.
/// </para>
/// </summary>
/// <param name="Mdc">
/// The major diagnostic category this code assigns when it is the principal
/// diagnosis. Zero means the code assigns no MDC of its own.
/// </param>
/// <param name="Severity">Whether the code is a CC, an MCC, or neither.</param>
/// <param name="Operands">The code's own operand list.</param>
/// <param name="DiagnosisCategoryOperands">
/// The code's diagnosis-category operand list, which is how most codes reach a
/// formula: about two thirds of CMS's diagnosis rows carry one of these and
/// only a quarter carry an operand list of their own.
/// </param>
/// <param name="HacOperands">
/// The code's hospital-acquired-condition operand list, used by the HAC
/// formulas rather than the DRG formulas.
/// </param>
/// <param name="SexMdcs">
/// For a sex-specific code, the MDC it assigns per sex, keyed by the UB-04 sex
/// digit (1 male, 2 female). Null for the great majority of codes, which are
/// not sex-specific.
///
/// <para>
/// This is not a validation table. It is how CMS assigns the MDC for these
/// codes at all: the same code means MDC 12 for a male stay and MDC 13 for a
/// female one, and <see cref="Mdc"/> does not apply. A code present here with
/// no entry for the stay's sex is the sex conflict.
/// </para>
/// </param>
public sealed record DiagnosisCodeAttributes(
    int Mdc,
    ComplicationLevel Severity,
    IReadOnlyList<string> Operands,
    IReadOnlyList<string> DiagnosisCategoryOperands,
    IReadOnlyList<string> HacOperands,
    IReadOnlyDictionary<int, int>? SexMdcs = null);

/// <summary>What CMS's tables say about one procedure code.</summary>
/// <param name="Operands">The code's operand list.</param>
public sealed record ProcedureCodeAttributes(IReadOnlyList<string> Operands);

/// <summary>
/// The code-to-operand tables a mask is built from.
///
/// <para>
/// This is an interface because the tables themselves cannot live in this
/// repository. They are CMS table data, distributed per requester, and the
/// package carries American Hospital Association copyright in the UB-04 code
/// descriptions. So the tables are imported at run time from a CMS release the
/// operator holds a licence for, and the engine depends only on this shape.
/// </para>
///
/// <para>
/// A code that is not in the tables is not an error here. It is a fact the
/// caller has to report, which is why these are <c>TryGet</c> methods and not
/// indexers returning an empty list. It is not a reason to refuse the stay:
/// the parity run settled that CMS groups a stay carrying an unknown
/// secondary diagnosis or procedure as though the code were absent, and
/// refuses an unknown principal through its own <c>invalid_pdx</c> edit during
/// grouping rather than ahead of it. An earlier version of this paragraph said
/// the opposite.
/// </para>
/// </summary>
public interface IMsDrgCodeAttributeSource
{
    /// <summary>The CMS grouper version these tables were imported for.</summary>
    string Version { get; }

    /// <summary>Looks up a diagnosis code.</summary>
    bool TryGetDiagnosis(string code, out DiagnosisCodeAttributes attributes);

    /// <summary>Looks up a procedure code.</summary>
    bool TryGetProcedure(string code, out ProcedureCodeAttributes attributes);
}
