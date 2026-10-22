using CloudHealthOffice.GrouperEngine.Formulas;

namespace CloudHealthOffice.GrouperEngine.Mask;

/// <summary>
/// What the mask builder produces for a stay: the mask its formulas are
/// evaluated against, the claim's codes in the order the formulas walk them,
/// and the codes the tables did not recognise.
/// </summary>
/// <param name="Mask">The operands the stay satisfies.</param>
/// <param name="Codes">
/// The claim's codes, principal diagnosis first, which the code-driven
/// operands read.
/// </param>
/// <param name="UnrecognisedCodes">
/// Codes absent from the tables, in the order they appeared. A caller must
/// treat a non-empty list as a reason to refuse: an unrecognised code is not
/// the same as an absent one, and guessing is how a grouper quietly
/// under-codes a stay.
/// </param>
public sealed record MsDrgMaskedClaim(
    AttributeMask Mask,
    ClaimCodes Codes,
    IReadOnlyList<string> UnrecognisedCodes);
