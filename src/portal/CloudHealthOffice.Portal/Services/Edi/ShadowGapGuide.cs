namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// For each reason CHO could not decide a shadow-adjudicated claim (the pend
/// or gap code claims-service records), what is missing and what to load to
/// close it: the portal page and the guide. Shown next to "Why CHO could not
/// decide" so a plan goes from a gap to the importer that fixes it.
/// </summary>
public static class ShadowGapGuide
{
    private const string Docs = "https://github.com/aurelianware/cloudhealthoffice/blob/main/docs/integration/";

    /// <summary>What kind of action a gap needs.</summary>
    public enum GapAction
    {
        /// <summary>CHO is missing plan data: load it, then run again.</summary>
        LoadData,

        /// <summary>A service CHO calls did not answer: check it, then run again.</summary>
        RunAgain,

        /// <summary>CHO pended for a person to look, as the core may have: compare with the core's own review.</summary>
        ExpectedReview,
    }

    public sealed record GapLink(string Text, string Href);

    public sealed record Gap(string Code, string Missing, string Fix, GapAction Action, IReadOnlyList<GapLink> Links);

    private static GapLink Page(string text, string href) => new(text, href);

    private static GapLink Guide(string text, string file) => new(text, Docs + file);

    /// <summary>claims-service's code for a claim CHO would hold only because it could not screen a provider.</summary>
    public const string ProviderScreeningCode = "PROVIDERSCREEN";

    private static readonly Dictionary<string, Gap> Gaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NOCONTRACT"] = new("NOCONTRACT",
            "No contract or fee schedule rate for the billing provider and service.",
            "Load the provider contracts (contract roster) and the fee schedules they point to. For Medicare-based contracts, load the Medicare physician fee schedule by locality. A shared state schedule from the fee schedule library can stand in until the plan's own arrives.",
            GapAction.LoadData,
            [Page("Fee schedules", "/fee-schedules"), Guide("Contract roster importer", "CONTRACT-ROSTER-IMPORTER.md"),
             Guide("Fee schedule CSV importer", "FEE-SCHEDULE-IMPORTER.md"), Guide("Medicare PFS importer", "MEDICARE-PFS-IMPORTER.md")]),
        ["MEMBERNOTLOADED"] = new("MEMBERNOTLOADED",
            "The member on the claim is not in CHO.",
            "Load members and coverage before claims: the CPCDS member and coverage files in the same drop, or the plan's 834.",
            GapAction.LoadData,
            [Page("Imports & EDI", "/edi-transactions"), Guide("Loading a plan's data: members and coverage", "LOADING-A-PLANS-DATA.md#4-members-and-coverage")]),
        ["PLANUNAVAIL"] = new("PLANUNAVAIL",
            "The member's benefit plan could not be read: usually no CHO benefit plan for the core's plan code.",
            "Create the plan's benefit plans (start from a plan template) and map the core's plan codes to them. If the plans exist, check that benefit-plan-service is up.",
            GapAction.LoadData,
            [Page("Plan templates", "/benefit-plans/templates"), Page("Benefit plans", "/benefit-plans"),
             Guide("Loading a plan's data: benefit plans and plan codes", "LOADING-A-PLANS-DATA.md#1-benefit-plans")]),
        ["NETWORK"] = new("NETWORK",
            "The provider is not in a CHO network, or not credentialed for it.",
            "Load the core's network roster (NPI, network, plan, dates, credentialed): it puts each provider in the plan's network the way the core has it. The CPCDS drop only creates reference-only providers from NPPES.",
            GapAction.LoadData,
            [Page("Providers", "/providers"), Guide("Network roster importer", "NETWORK-ROSTER-IMPORTER.md"),
             Guide("Loading a plan's data: providers and network", "LOADING-A-PLANS-DATA.md#3-providers-and-network")]),
        ["NOAUTH"] = new("NOAUTH",
            "The service needs a prior authorization and CHO has none on file.",
            "Load the plan's authorizations: X12 278 request/response pairs or the authorization CSV, in the same daily drop.",
            GapAction.LoadData,
            [Page("Imports & EDI", "/edi-transactions"), Guide("Authorization import", "AUTHORIZATION-IMPORT.md"),
             Guide("Prior auth rules importer", "PRIOR-AUTH-RULES-IMPORTER.md")]),
        ["RETROELIG"] = new("RETROELIG",
            "The member's plan changed retroactively over the date of service.",
            "Check the member's coverage spans against the core's; load the corrected member and coverage file.",
            GapAction.LoadData,
            [Page("Members", "/members"), Guide("Loading a plan's data: members and coverage", "LOADING-A-PLANS-DATA.md#4-members-and-coverage")]),
        ["ACCUMCOMMIT"] = new("ACCUMCOMMIT",
            "Accumulators could not be read or prepared for the member.",
            "Check accumulator service health and run again. CHO starts imported members' deductibles at zero until an accumulator baseline is loaded.",
            GapAction.RunAgain, []),
        ["TIMELY"] = new("TIMELY",
            "Timely filing: the claim looks late by CHO's rules.",
            "Check the received date column in the CPCDS claims file and the plan's timely filing limit in the benefit plan.",
            GapAction.LoadData,
            [Page("Benefit plans", "/benefit-plans"), Guide("CPCDS file drop importer", "CPCDS-FILE-DROP-IMPORTER.md")]),
        ["COB"] = new("COB",
            "The member has other coverage and CHO needs the payer order.",
            "Load the member's other coverage, or compare with how the core coordinated benefits.",
            GapAction.ExpectedReview, [Page("Members", "/members")]),
        ["TPL"] = new("TPL",
            "Third-party liability on file for a Medicaid member.",
            "Load the state's TPL file so CHO cost-avoids or pays and chases as the core does.",
            GapAction.LoadData, [Guide("Medicaid TPL", "MEDICAID-TPL.md")]),
        ["SUBRO"] = new("SUBRO", "Possible subrogation (an accident or injury claim).",
            "A person reviews it in CHO, as the core may have.", GapAction.ExpectedReview, []),
        ["DUPLICATE"] = new("DUPLICATE", "CHO found what looks like the same claim already on file.",
            "Check whether the extract sent the claim twice under different ids.", GapAction.ExpectedReview, []),
        ["MEDREVIEW"] = new("MEDREVIEW", "The claim needs medical review under the plan's rules.",
            "Expected: compare with the core's own review outcome.", GapAction.ExpectedReview, []),
        ["PLANEDIT"] = new("PLANEDIT", "One of the plan's own claim edits pended it.",
            "Expected review; if the core never pends these, check the edit's settings in the benefit plan.",
            GapAction.ExpectedReview, [Page("Benefit plans", "/benefit-plans")]),
        ["DRGREVIEW"] = new("DRGREVIEW", "The inpatient claim's DRG needs review.",
            "Check the DRG pricing settings for the plan.", GapAction.ExpectedReview, [Guide("Medicaid DRG pricing", "MEDICAID-DRG-PRICING.md")]),
        ["CONFIGUNAVAIL"] = new("CONFIGUNAVAIL", "CHO could not read the plan's inpatient grouping setting.",
            "Set the plan's DRG grouping settings, then run again.", GapAction.LoadData,
            [Guide("Medicaid DRG pricing", "MEDICAID-DRG-PRICING.md"), Guide("Licensed grouper", "LICENSED-GROUPER-CONTRACT.md")]),
        ["GROUPERUNAVAIL"] = new("GROUPERUNAVAIL", "The DRG grouper did not answer.",
            "Check the grouper connection, then run again.", GapAction.RunAgain, [Guide("Licensed grouper", "LICENSED-GROUPER-CONTRACT.md")]),
        ["AIDCODE"] = new("AIDCODE", "The Medicaid member has no aid code (or birth date) for the date of service.",
            "Load members' aid codes from the state's eligibility file.", GapAction.LoadData, [Page("Members", "/members")]),
        ["STATEDENTAL"] = new("STATEDENTAL", "A state dental carve-out rule applies to this member.",
            "Expected for affected members; check the plan's state dental setting.", GapAction.ExpectedReview, []),
        ["SPENDDOWN"] = new("SPENDDOWN", "The Medicaid member has an unmet spend-down.",
            "Load the member's spend-down from the state file.", GapAction.LoadData, [Page("Members", "/members")]),
        ["SCRUB"] = new("SCRUB", "The claim failed CHO's data checks (codes, NPIs, dates).",
            "Check the claim's columns in the CPCDS file, and the column map for a renamed column.",
            GapAction.LoadData, [Page("Imports & EDI", "/edi-transactions"), Guide("CPCDS file drop importer", "CPCDS-FILE-DROP-IMPORTER.md")]),
        ["PRICING"] = new("PRICING", "The pricing service did not answer, or answered with nothing usable.",
            "Check benefit-plan-service (fee schedules), then run again.", GapAction.RunAgain, []),
        ["PAUNAVAIL"] = new("PAUNAVAIL", "The prior authorization lookup did not answer.",
            "Check authorization-service, then run again.", GapAction.RunAgain, []),
        [ProviderScreeningCode] = new(ProviderScreeningCode,
            "CHO could not screen the billing or rendering provider against the OIG and SAM exclusion lists and NPPES, so it would hold the claim for review.",
            "Turn on exclusion screening in provider-verification-service (OIG LEIE, and SAM with an API key) and load the plan's providers. A confirmed exclusion is compared as the denial it is.",
            GapAction.LoadData,
            [Page("Providers", "/providers"), Guide("Shadow adjudication: provider screening", "SHADOW-ADJUDICATION.md#provider-screening")]),
        ["SVCUNAVAIL"] = new("SVCUNAVAIL", "A service CHO calls during adjudication did not answer.",
            "Run again; if it repeats, the reason names the service.", GapAction.RunAgain, []),
    };

    /// <summary>The guide entry for a gap code; a stage rejection or unknown code gets a general entry.</summary>
    public static Gap For(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code) && Gaps.TryGetValue(code.Trim(), out var gap))
            return gap;
        if (code is { } c && c.StartsWith("REJECT:", StringComparison.OrdinalIgnoreCase))
            return new Gap(c, $"CHO's {c["REJECT:".Length..]} stage rejected the claim: it lacks something that stage needs.",
                "Open an example claim's reason below; it names the missing field or setting.", GapAction.LoadData,
                [Guide("Loading a plan's data", "LOADING-A-PLANS-DATA.md")]);
        return new Gap(code ?? "UNKNOWN", "CHO pended the claim with this code.",
            "Open an example claim's reason below.", GapAction.LoadData, [Guide("Shadow adjudication", "SHADOW-ADJUDICATION.md")]);
    }

    public static string ActionLabel(GapAction action) => action switch
    {
        GapAction.LoadData => "Load data",
        GapAction.RunAgain => "Run again",
        _ => "Expected review",
    };
}
