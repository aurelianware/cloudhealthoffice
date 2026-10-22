# CloudHealthOffice.ClinicalCriteria

The clinical criteria seam for utilization management.

Cloud Health Office ships **no licensed criteria content**. InterQual and MCG
guidelines are licensed per customer and cannot be redistributed. What this
project provides is the integration in full — the request CHO sends, the
evaluation it expects back, the routing that decides which source is asked,
and the behaviour when that source is slow, broken or absent — so that adding
a licensed source is writing one transport class, not changing the platform.

## The pieces

| Type | Role |
|---|---|
| `IClinicalCriteriaService` | What callers use. Resolves the provider, applies the timeout, never throws |
| `IClinicalCriteriaProvider` | One source of criteria |
| `CriteriaRequest` | Everything CHO sends about a review |
| `CriteriaEvaluation` | One normalized answer, persisted as an audit record |
| `LocalRulesCriteriaProvider` | In-repo default (`cho-local`), always configured |
| `InterQualCriteriaProvider`, `McgCriteriaProvider` | Licensed sources, inert without a connector |

## The fail-open rule

Every path that cannot produce a clinical conclusion produces
`CriteriaRecommendation.Indeterminate` with a `FailureReason`, and an
indeterminate evaluation routes to a human reviewer. A provider that is
unregistered, unconfigured, timed out or throwing never produces a denial.

`DoesNotMeetCriteria` is not a denial either. It always carries
`RequiresPhysicianReview`, which `ClinicalCriteriaService` sets whether or not
the provider did: an adverse determination needs a reviewer of the same or
greater clinical qualification, and that is a regulatory requirement, not a
configuration choice.

## Routing

```json
"ClinicalCriteria": {
  "DefaultProvider": "cho-local",
  "EvaluationTimeout": "00:00:10",
  "Routing": [
    { "Lob": "Medicare", "ReviewType": "Concurrent", "Provider": "interqual" },
    { "TenantId": "acme", "Provider": "mcg" }
  ],
  "InterQual": { "Enabled": false, "BaseUrl": null, "CredentialSecretName": null },
  "Mcg":       { "Enabled": false, "BaseUrl": null, "CredentialSecretName": null }
}
```

A rule matches when every field it sets equals the request's; fields left out
match anything. The rule that constrains the most fields wins, and ties go to
the first configured. With no matching rule the request goes to
`DefaultProvider`.

Credentials are never held in this configuration. `CredentialSecretName` names
a secret, which the host resolves through its own secret provider.

## Adding a licensed source

1. Take the licence and the vendor's API credentials. Store the credential in
   the deployment's secret store and name it in `CredentialSecretName`.
2. Write a class implementing `IClinicalCriteriaProvider` whose `ProviderId` is
   `interqual` or `mcg`. It maps `CriteriaRequest` onto the vendor request and
   the vendor response onto `CriteriaEvaluation`. Deriving from
   `LicensedCriteriaProvider` gives it the configuration and the
   `IsConfigured` check; only `EvaluateAsync` has to be written.
3. Register it **after** `AddClinicalCriteria(configuration)`. The last
   registration of a provider id wins, so no existing registration needs
   removing.
4. Set `Enabled`, `BaseUrl` and `CredentialSecretName` for that provider, and
   route traffic to it.

Nothing above the provider changes, because nothing above the provider knows
which source answered.

### What the connector must fill in

- `Recommendation` — the vendor's conclusion, mapped onto the enum. Map an
  unrecognised vendor status to `Indeterminate`, never to a denial.
- `Guideline` — the guideline id, title and content version actually applied.
  A determination has to be reconstructable years later, when the content
  version has moved on.
- `Rationale` — the vendor's own wording. It is quoted to the member in an
  adverse determination letter, so it is not paraphrased.
- `OutstandingQuestions` — criteria the vendor could not evaluate for want of
  clinical detail. These drive the documentation request back to the provider.
- `AuditPayload` — the vendor response verbatim.
- `RecommendedLengthOfStayDays` / `NextReviewInDays` — where the guideline
  gives them. Concurrent review uses both.

### What it must not do

- Swallow an error. Throw; `ClinicalCriteriaService` logs it and pends.
- Return `MeetsCriteria` when the vendor was unreachable.
- Hold its own timeout or retry loop. The service owns the clock, so a vendor
  cannot hold a prior-authorization decision deadline open.

## The in-repo provider is not a clinical guideline

`cho-local` runs `CloudHealthOffice.PriorAuthRuleEngine`, which holds coverage
and benefit rules: does this procedure require authorization here, is the
member within the visit limit. Those rules are not competent to make a
medical-necessity determination, so a denying rule maps to `NotCovered` — a
coverage answer — and never to `DoesNotMeetCriteria`. A rule that reaches no
conclusion maps to `Indeterminate` and pends.

It exists so the seam is exercised in every environment and no deployment
depends on a licence to run. It is not a substitute for licensed content, and
the platform does not present it as one.
