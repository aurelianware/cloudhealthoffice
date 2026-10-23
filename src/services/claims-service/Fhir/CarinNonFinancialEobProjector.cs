using System.Text.Json.Nodes;
using ClaimsService.Models;

namespace ClaimsService.Fhir;

/// <summary>
/// The CHO claim record as a CARIN Blue Button <b>non-financial</b>
/// <c>ExplanationOfBenefit</c> — the payload CMS-0057-F's Provider Access API
/// is expected to carry.
///
/// <para>
/// This is not the same resource as <see cref="ExplanationOfBenefitProjector"/>
/// produces. That one is the member-facing Patient Access EOB and carries what
/// adjudication decided, money included. CARIN BB defines a parallel set of
/// "Basis" profiles that require everything <i>but</i> the dollar amounts, and
/// those are what a payer sends another provider: the clinical and
/// administrative record of the encounter without the plan's financial terms.
/// PDex points Provider Access at them, and it puts <b>both</b> claims and
/// encounters in them — there is no FHIR <c>Claim</c> or <c>Encounter</c> in a
/// Provider Access response. CHO's <see cref="ClaimFhirProjector"/> and
/// encounter-service's Encounter projection remain the CHO-native source
/// records behind this projection, not the payload itself.
/// </para>
///
/// <para>
/// So nothing monetary is emitted here, and that is a requirement rather than a
/// simplification: no <c>total</c>, no <c>item.net</c> or <c>unitPrice</c>, no
/// adjudication amount of any kind. A test asserts the absence.
/// </para>
///
/// <para>
/// Which profile an instance declares follows the claim type, as CARIN BB
/// splits them:
/// </para>
/// <list type="table">
///   <item><term><see cref="ClaimType.Professional"/></term>
///     <description>Professional NonClinician Basis.</description></item>
///   <item><term><see cref="ClaimType.Institutional"/></term>
///     <description>Inpatient or Outpatient Institutional Basis, decided by the
///     bill classification digit of the type of bill — see
///     <see cref="IsInpatient"/>.</description></item>
///   <item><term><see cref="ClaimType.Dental"/></term>
///     <description>Oral Basis.</description></item>
/// </list>
///
/// <para>
/// Known conformance limits, stated rather than papered over, and checked by
/// the HL7 validator in CI (see <c>scripts/interop/fhir-validate.sh</c>):
/// <c>insurer</c>, the header <c>provider</c> and <c>insurance.coverage</c> are
/// references by identifier and display, because CHO publishes no
/// <c>Organization</c> or <c>Coverage</c> resource for them to point at, and a
/// billing NPI may denote either a group or an individual; the C4BB
/// <c>targetProfile</c>s on those elements therefore go unsatisfied.
/// <c>careTeam.provider</c> does carry a literal <c>Practitioner</c> reference.
/// The
/// Inpatient profile requires a revenue code on every line, which an 837I
/// carries but the model allows to be null.
/// </para>
/// </summary>
public sealed class CarinNonFinancialEobProjector : ICarinNonFinancialEobProjector
{
    /// <summary>The Basis profile canonicals, by claim type.</summary>
    public const string ProfessionalBasisProfile = CarinEobBasis.ProfessionalBasisProfile;
    public const string InpatientInstitutionalBasisProfile = CarinEobBasis.InpatientInstitutionalBasisProfile;
    public const string OutpatientInstitutionalBasisProfile = CarinEobBasis.OutpatientInstitutionalBasisProfile;
    public const string OralBasisProfile = CarinEobBasis.OralBasisProfile;

    /// <summary>
    /// The shaping is <see cref="CarinEobBasis"/>'s, shared with the financial
    /// projection. This projector's whole contribution is to ask for it with no
    /// money: it passes none of the callbacks, so nothing monetary can reach the
    /// instance by accident.
    /// </summary>
    public JsonObject Project(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return CarinEobBasis.Scaffold(claim, CarinEobBasis.Shape(claim, financial: false));
    }
}
