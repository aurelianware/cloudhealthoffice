using System.Globalization;
using System.Text;

namespace DemoPersonaSeeder;

/// <summary>Writes the presenter's script: the cast, the IDs to type, and what the seeded claims did.</summary>
public static class DemoScript
{
    public static string Render(DemoDataset data, string tenantId, SeedRun? run)
    {
        var sb = new StringBuilder();
        string D(DateTime d) => d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);

        sb.AppendLine("# Demo script: synthetic personas");
        sb.AppendLine();
        sb.AppendLine($"Tenant `{tenantId}`, stories dated relative to {D(data.AsOf)}. Every person, provider and company below is invented.");
        sb.AppendLine(run is null
            ? "This is a dry run: nothing was written to the services."
            : run.HasFailures
                ? "Some seeding steps failed; see the end of this script."
                : "All seeding steps succeeded or were already in place.");
        sb.AppendLine();

        sb.AppendLine("## Stories");
        sb.AppendLine();
        foreach (var story in data.Stories)
        {
            sb.AppendLine($"### {story.Title}");
            sb.AppendLine();
            sb.AppendLine($"*For:* {story.Role}");
            sb.AppendLine();
            for (var i = 0; i < story.Steps.Count; i++)
                sb.AppendLine($"{i + 1}. {story.Steps[i]}");
            sb.AppendLine();
        }

        sb.AppendLine("## Members");
        sb.AppendLine();
        sb.AppendLine("| Member ID | Name | DOB | Plan | Group | Relationship | Card IDs | PCP |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var m in data.Members)
        {
            var ids = m.Identifiers.Count == 0 ? "" : string.Join(", ", m.Identifiers.Select(i => $"{i.Type} {i.Value}"));
            var pcp = m.PcpProviderKey is null ? "" : data.Provider(m.PcpProviderKey).DisplayName;
            var relationship = m.RelationshipCode switch { "18" => "Subscriber", "01" => "Spouse", "19" => "Child", var c => c };
            sb.AppendLine($"| {m.MemberId} | {m.FullName} | {D(m.DateOfBirth)} | {data.Plan(m.PlanKey).Name} | {m.GroupNumber} | {relationship} | {ids} | {pcp} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Providers");
        sb.AppendLine();
        sb.AppendLine("| Name | NPI | TIN | Specialty | City |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var p in data.Providers)
            sb.AppendLine($"| {p.DisplayName} | {p.Npi} | {p.TaxId[..2]}-{p.TaxId[2..]} | {p.Specialty} | {p.Address.City} |");
        sb.AppendLine();

        sb.AppendLine("## Employer groups and plans");
        sb.AppendLine();
        foreach (var s in data.Sponsors)
            sb.AppendLine($"- **{s.GroupNumber}** {s.EmployerName}: ${s.PremiumPerMember.ToString("N0", CultureInfo.InvariantCulture)} per member per month, billed on day {s.BillingDay}, {s.GracePeriodDays}-day grace period.");
        foreach (var p in data.Plans)
            sb.AppendLine($"- **{p.PlanId}** {p.Name} ({p.LineOfBusiness} {p.PlanType}).");
        sb.AppendLine();

        sb.AppendLine("## Authorizations");
        sb.AppendLine();
        sb.AppendLine("| Auth # | Member | Service | Dates | Priority | Status |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var a in data.Authorizations)
        {
            var services = string.Join(", ", a.Services.Select(s => $"{s.ProcedureCode} {s.Description} ×{s.Units:0.#}"));
            var dates = a.To is { } to ? $"{D(a.From)} – {D(to)}" : D(a.From);
            var priority = a.LevelOfService == "U" ? "Urgent" : "Standard";
            var status = a.Decision is null ? "Submitted, awaiting decision" : $"{a.Decision} ({a.ApprovedUnits:0.#} units)";
            sb.AppendLine($"| {a.Number} | {data.Member(a.MemberId).FullName} | {services} | {dates} | {priority} | {status} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Claims");
        sb.AppendLine();
        sb.AppendLine("| Claim # | Provider's account # | Member | Provider | DOS | Billed | Expected | Status after seeding |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var c in data.Claims)
        {
            var status = run is null ? "(dry run)" : run.ClaimStatuses.GetValueOrDefault(c.ClaimNumber, "not created");
            var billed = c.Lines.Sum(l => l.Charge).ToString("C0", CultureInfo.GetCultureInfo("en-US"));
            sb.AppendLine($"| {c.ClaimNumber} | {c.PatientAccountNumber} | {data.Member(c.MemberId).FullName} | {data.Provider(c.BillingProviderKey).DisplayName} | {D(c.ServiceDateFrom)} | {billed} | {c.Intent} | {status} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Appeals");
        sb.AppendLine();
        foreach (var a in data.Appeals)
            sb.AppendLine($"- **{a.AppealNumber}** ({a.AppealType}) on {a.ClaimNumber}: {a.Reason}");
        sb.AppendLine();

        sb.AppendLine("## Not seeded, by design");
        sb.AppendLine();
        sb.AppendLine("- **Payments.** A payment run needs a trading partner for each payee and approval by a second user, so the seeder stops at approved claims. Run one from the portal (Finance → Payment Runs) to give Canyon Ridge Orthopedics a check number.");
        sb.AppendLine("- **Past-due premium.** Billing sets the due date in the future when the billing day has passed, so Copper State's invoice becomes past due on its due date and delinquent after the grace period, when delinquency processing runs.");
        sb.AppendLine("- **Claim outcomes** come from the tenant's adjudication rules. The Expected column is the story; the last column is what actually happened.");
        sb.AppendLine();

        if (run is not null && run.Steps.Any(s => s.Outcome == StepOutcome.Failed))
        {
            sb.AppendLine("## Failed steps");
            sb.AppendLine();
            foreach (var s in run.Steps.Where(s => s.Outcome == StepOutcome.Failed))
                sb.AppendLine($"- {s.Area} `{s.Key}`: {s.Detail}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
