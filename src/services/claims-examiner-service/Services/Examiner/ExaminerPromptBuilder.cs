using System.Text;
using System.Text.Json.Nodes;
using ClaimsExaminerService.Models;
using ClaimsExaminerService.Services.Anthropic;
using CloudHealthOffice.Events;

namespace ClaimsExaminerService.Services.Examiner;

public interface IExaminerPromptBuilder
{
    /// <summary>The version string written into AiExamination.PromptVersion.</summary>
    string PromptVersion { get; }

    string BuildSystemPrompt();

    /// <summary>
    /// Build the per-claim user message. Inputs are: the full claim, the
    /// specific NCCI edit failure being reasoned about, and (optionally)
    /// historical RFAI activity for the billing provider on this edit type.
    /// The builder is intentionally deterministic — same inputs → same prompt —
    /// so model behavior changes can be attributed to model version, not prompt drift.
    ///
    /// <paramref name="rfaiHistory"/> may be null when the history client returns
    /// nothing for this provider/edit pair; the builder treats null as a neutral
    /// signal and simply omits the history section, rather than fabricating one.
    /// </summary>
    string BuildUserMessage(
        ClaimSnapshot claim,
        NcciEditFailureSnapshot edit,
        ProviderRfaiHistory? rfaiHistory = null);

    /// <summary>The forced-tool-use schema the model must populate.</summary>
    AnthropicTool BuildRecommendationTool();
}

public class ExaminerPromptBuilder : IExaminerPromptBuilder
{
    public string PromptVersion => "ncci-pend-v1";

    public string BuildSystemPrompt() => """
        You are an experienced healthcare claims examiner reviewing a claim that was
        pended by the deterministic NCCI Procedure-to-Procedure (PTP) edit engine.

        Your job is narrow and well-defined:

        1. The deterministic engine has ALREADY identified that two procedure codes
           on this claim form an NCCI Column 1 / Column 2 pair (a bundling edit).
           You do NOT need to second-guess whether the edit fired correctly.

        2. The ONLY question you are answering is: based on the documentation present
           on this claim (modifiers, diagnosis pointers, place of service, line-level
           service dates), should the bundling edit be overridden by an appropriate
           NCCI-associated modifier (-59, XE, XS, XP, XU)?

        3. NCCI modifier indicator semantics:
           - The PTP edit being shown to you has ModifierIndicator = 1, meaning a
             -59 / X{EPSU} modifier is permitted to override the bundle WHEN the
             clinical circumstances support distinct procedural service.
           - You must cite the specific NCCI Manual policy basis when recommending
             override (e.g., "NCCI Policy Manual Ch.1 §F" for -59 use).

        4. Disposition rules — be conservative:
           - "Approve" only when the claim documentation already contains a valid
             distinct-procedural-service modifier on the Column 2 line AND the
             diagnosis pointers / service dates / place of service support its use.
             You are saying "the edit should be overridden and paid as billed."
           - "RequestInfo" when distinct procedural service is plausible but the
             current documentation is insufficient to confirm it (e.g., no modifier,
             or modifier present but ambiguous diagnostic linkage). You are saying
             "the provider should be asked to substantiate or correct."
           - "Deny" when the documentation actively contradicts a distinct service
             claim (e.g., same diagnosis, same anatomic site, identical service date,
             no modifier, no other distinguishing factor).
           - "EscalateToHuman" when you cannot reach a confident conclusion. This
             is the safe default. Use it freely; a human reviewer is the fallback.

        5. Confidence calibration:
           - 0.90+ : You would stake your professional reputation on this call.
           - 0.70–0.89 : Strong evidence in the documentation, minor uncertainty.
           - 0.50–0.69 : Plausible but the human reviewer should weigh in.
           - Below 0.50 : You should be using EscalateToHuman.

        6. Rationale rules:
           - Be concise. 3–6 sentences.
           - Reference specific evidence on the claim (line numbers, modifiers,
             diagnosis codes) — never speak in generalities.
           - Never invent facts. If a piece of documentation is missing, say so.

        Output exclusively via the recommend_disposition tool. Do not produce any
        free-text turn — the tool call IS your response.
        """;

    public string BuildUserMessage(
        ClaimSnapshot claim,
        NcciEditFailureSnapshot edit,
        ProviderRfaiHistory? rfaiHistory = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Pended Claim — NCCI Bundling Edit");
        sb.AppendLine();
        sb.AppendLine("## Edit Failure Detected by NCCI Engine");
        sb.AppendLine($"- Edit type: {edit.EditType}");
        sb.AppendLine($"- Rule id: {edit.RuleId}");
        sb.AppendLine($"- Column 1 (primary) procedure: {edit.Column1Code}");
        sb.AppendLine($"- Column 2 (bundled) procedure: {edit.Column2Code}");
        sb.AppendLine($"- Affected line numbers: {string.Join(", ", edit.AffectedLineNumbers)}");
        sb.AppendLine($"- Modifier override modifier already present at submission: {edit.ModifierOverridePresent}");
        if (!string.IsNullOrEmpty(edit.SuggestedCarc))
        {
            sb.AppendLine($"- Suggested CARC if denied: {edit.SuggestedCarc}");
        }
        if (!string.IsNullOrEmpty(edit.Message))
        {
            sb.AppendLine($"- Engine message: {edit.Message}");
        }
        sb.AppendLine();

        sb.AppendLine("## Claim Header");
        sb.AppendLine($"- Claim id: {claim.Id}");
        sb.AppendLine($"- Claim number: {claim.ClaimNumber}");
        sb.AppendLine($"- Member id: {claim.MemberId}");
        sb.AppendLine($"- Billing provider NPI: {claim.BillingProviderNPI}");
        if (!string.IsNullOrEmpty(claim.BillingProviderName))
        {
            sb.AppendLine($"- Billing provider name: {claim.BillingProviderName}");
        }
        sb.AppendLine($"- Place of service: {claim.PlaceOfServiceCode}");
        sb.AppendLine($"- Service date range: {claim.ServiceDateFrom:yyyy-MM-dd} to {claim.ServiceDateTo:yyyy-MM-dd}");
        sb.AppendLine($"- Total billed amount: {claim.TotalChargeAmount:C}");
        sb.AppendLine();

        sb.AppendLine("## Diagnosis Codes (ICD-10)");
        if (claim.DiagnosisCodes.Count == 0)
        {
            sb.AppendLine("- (none on claim)");
        }
        else
        {
            foreach (var dx in claim.DiagnosisCodes)
            {
                var desc = string.IsNullOrEmpty(dx.Description) ? "" : $" — {dx.Description}";
                sb.AppendLine($"- Pointer {dx.PointerNumber}: {dx.Code}{desc}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Service Lines");
        foreach (var line in claim.ClaimLines)
        {
            var modList = line.Modifiers.Count == 0 ? "(none)" : string.Join(", ", line.Modifiers);
            var dxPtrs = line.DiagnosisPointers.Count == 0 ? "(none)" : string.Join(", ", line.DiagnosisPointers);
            sb.AppendLine($"- Line {line.LineNumber}: CPT/HCPCS {line.ProcedureCode}");
            if (!string.IsNullOrEmpty(line.ProcedureDescription))
            {
                sb.AppendLine($"    Description: {line.ProcedureDescription}");
            }
            sb.AppendLine($"    Modifiers: {modList}");
            sb.AppendLine($"    Diagnosis pointers: {dxPtrs}");
            sb.AppendLine($"    Service date: {line.ServiceDateFrom:yyyy-MM-dd}");
            sb.AppendLine($"    Place of service: {line.PlaceOfServiceCode ?? claim.PlaceOfServiceCode}");
            sb.AppendLine($"    Units: {line.Units}, Charge: {line.ChargeAmount:C}");
        }
        sb.AppendLine();

        // Optional context block: provider's historical RFAI behavior for this
        // edit type. Rendered only when the orchestrator has data — a missing
        // section is itself a signal to the model (no history → no information,
        // not "good history"). We never invent a history.
        if (rfaiHistory is not null && rfaiHistory.TotalRfaisSent > 0)
        {
            sb.AppendLine("## Provider RFAI History (for this edit type)");
            sb.AppendLine($"- Total RFAIs sent for this edit type: {rfaiHistory.TotalRfaisSent}");
            sb.AppendLine($"- Provider responded: {rfaiHistory.TotalResponded} ({rfaiHistory.ResponseRatePct:F0}% response rate)");
            sb.AppendLine($"- Average response time: {rfaiHistory.AvgResponseDays} days");
            if (rfaiHistory.LastRfaiSentAt.HasValue)
            {
                sb.AppendLine($"- Most recent RFAI: {rfaiHistory.LastRfaiSentAt.Value:yyyy-MM-dd}");
            }
            sb.AppendLine("- Use this history as a soft signal only: a chronically non-responsive provider is a stronger candidate for EscalateToHuman than a high-responder, all else equal. It is NEVER sufficient grounds on its own to recommend Approve or Deny.");
            sb.AppendLine();
        }

        sb.AppendLine("## Task");
        sb.AppendLine($"Decide whether the NCCI bundling edit between {edit.Column1Code} (Column 1) and {edit.Column2Code} (Column 2) should be overridden by a -59 / X{{EPSU}} distinct-procedural-service modifier, or whether the bundling should stand. Use the recommend_disposition tool exclusively to respond.");

        return sb.ToString();
    }

    public AnthropicTool BuildRecommendationTool()
    {
        // JSON schema the model is forced to populate. Keep it strict —
        // every field has a clear purpose, and unknown extra fields will
        // simply be ignored on parse so the schema can grow without breaking
        // older recommendations stored on existing claims.
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["recommended_disposition"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("Approve", "Deny", "RequestInfo", "EscalateToHuman"),
                    ["description"] = "The disposition the AI examiner recommends for this pended claim."
                },
                ["confidence_score"] = new JsonObject
                {
                    ["type"] = "number",
                    ["minimum"] = 0,
                    ["maximum"] = 1,
                    ["description"] = "Self-reported confidence in the recommendation, 0.0 to 1.0."
                },
                ["rationale"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Concise (3-6 sentence) plain-English explanation citing specific claim evidence."
                },
                ["policy_citations"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Specific NCCI policy / manual references that support the recommendation."
                }
            },
            ["required"] = new JsonArray(
                "recommended_disposition",
                "confidence_score",
                "rationale",
                "policy_citations")
        };

        return new AnthropicTool(
            name: "recommend_disposition",
            description: "Record the AI examiner's recommended disposition for the pended claim.",
            inputSchema: schema);
    }
}
