using Hl7.Fhir.Model;

namespace CloudHealthOffice.Portal.Tests.Services.Dtr;

/// <summary>A small DTR form with the shapes CHO's library uses.</summary>
internal static class DtrFixtures
{
    public const string Canonical = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-test|1.0.0";
    public const string Hcpcs = "https://www.hcpcs.codes";
    public const string ReasonValueSet = "https://cloudhealthoffice.com/fhir/ValueSet/test-reasons";

    public static Questionnaire FormDefinition() => new()
    {
        Id = "pa-test",
        Url = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-test",
        Version = "1.0.0",
        Title = "Prior authorization: test equipment",
        Status = PublicationStatus.Active,
        UseContext =
        [
            new UsageContext
            {
                Code = new Coding("http://terminology.hl7.org/CodeSystem/usage-context-type", "focus"),
                Value = new CodeableConcept { Coding = [new Coding(Hcpcs, "E1390", "Oxygen concentrator")] },
            },
        ],
        Item =
        [
            new Questionnaire.ItemComponent
            {
                LinkId = "1", Text = "Equipment", Type = Questionnaire.QuestionnaireItemType.Choice, Required = true,
                AnswerOption =
                [
                    new Questionnaire.AnswerOptionComponent { Value = new Coding(Hcpcs, "E1390", "Oxygen concentrator") },
                    new Questionnaire.AnswerOptionComponent { Value = new Coding(Hcpcs, "E0601", "CPAP") },
                ],
            },
            new Questionnaire.ItemComponent
            {
                LinkId = "1a", Text = "Flow rate (L/min)", Type = Questionnaire.QuestionnaireItemType.Decimal, Required = true,
                EnableWhen =
                [
                    new Questionnaire.EnableWhenComponent
                    {
                        Question = "1", Operator = Questionnaire.QuestionnaireItemOperator.Equal,
                        Answer = new Coding(Hcpcs, "E1390"),
                    },
                ],
            },
            new Questionnaire.ItemComponent
            {
                LinkId = "2", Text = "Clinical history", Type = Questionnaire.QuestionnaireItemType.Group,
                Item =
                [
                    new Questionnaire.ItemComponent { LinkId = "2.1", Text = "Diagnosis summary", Type = Questionnaire.QuestionnaireItemType.Text, Required = true },
                    new Questionnaire.ItemComponent { LinkId = "2.2", Text = "Months of symptoms", Type = Questionnaire.QuestionnaireItemType.Integer },
                    new Questionnaire.ItemComponent { LinkId = "2.3", Text = "Reasons", Type = Questionnaire.QuestionnaireItemType.Choice, Repeats = true, AnswerValueSet = ReasonValueSet },
                ],
            },
            new Questionnaire.ItemComponent
            {
                LinkId = "3", Text = "Home assessment done", Type = Questionnaire.QuestionnaireItemType.Boolean,
                Initial = [new Questionnaire.InitialComponent { Value = new FhirBoolean(false) }],
            },
            new Questionnaire.ItemComponent
            {
                LinkId = "4", Text = "Internal", Type = Questionnaire.QuestionnaireItemType.String,
                Extension = [new Extension("http://hl7.org/fhir/StructureDefinition/questionnaire-hidden", new FhirBoolean(true))],
            },
        ],
    };

    public static ValueSet ReasonsValueSet() => new()
    {
        Url = ReasonValueSet,
        Status = PublicationStatus.Active,
        Expansion = new ValueSet.ExpansionComponent
        {
            Timestamp = "2026-11-01",
            Contains =
            [
                new ValueSet.ContainsComponent { System = "http://snomed.info/sct", Code = "1", Display = "Hypoxemia" },
                new ValueSet.ContainsComponent { System = "http://snomed.info/sct", Code = "2", Display = "COPD" },
            ],
        },
    };

    public static Patient Member(string memberId = "MBR-1001") => new()
    {
        Id = memberId,
        Name = [new HumanName { Use = HumanName.NameUse.Official, Family = "Rivera", Given = ["Ana"] }],
        BirthDate = "1984-03-07",
    };

    public static Coverage MemberCoverage(string memberId = "MBR-1001") => new()
    {
        Id = "cov-1",
        Status = FinancialResourceStatusCodes.Active,
        Beneficiary = new ResourceReference($"Patient/{memberId}"),
        Payor = [new ResourceReference("Organization/plan")],
        Type = new CodeableConcept("https://nahdo.org/sopt", "2", "Medicaid"),
    };
}
