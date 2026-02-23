---
name: "[v4.0] Member and Provider Portals"
about: Develop self-service portals for members and providers
title: "[v4.0] Develop Member and Provider Portals with Self-Service Features"
labels: enhancement, ui, v4.0, priority-medium
assignees: aurelianware
---

## Overview
Create dedicated portals for members (eligibility checks, benefits viewing) and providers (claims submission, performance metrics). Extend the existing Blazor admin portal, integrating tenant management for authentication and Stripe for subscription-gated features.

## Objectives
- ✅ Member portal with eligibility, benefits, prior auth requests
- ✅ Provider portal with claims submission, directory search, performance dashboard
- ✅ Azure AD B2C authentication with tenant isolation
- ✅ Stripe-gated premium features (advanced reporting)
- ✅ Mobile-responsive UI (prepare for future native apps)
- ✅ Real-time updates via SignalR (claim status notifications)

## Architecture

```
┌─────────────────────────────────────────────────┐
│         Cloud Health Office Portals             │
├─────────────────────────────────────────────────┤
│                                                 │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────┐│
│  │   Admin     │  │   Member    │  │ Provider ││
│  │   Portal    │  │   Portal    │  │  Portal  ││
│  │  (Existing) │  │    (New)    │  │  (New)   ││
│  └──────┬──────┘  └──────┬──────┘  └────┬─────┘│
│         │                │                │      │
│         └────────────────┴────────────────┘      │
│                          │                       │
│                   Azure AD B2C                   │
│                   Tenant Context                 │
│                          │                       │
│         ┌────────────────┴────────────────┐      │
│         │                                  │      │
│    ┌────▼────┐  ┌──────────┐  ┌──────────▼───┐  │
│    │ Member  │  │ Coverage │  │  Claims      │  │
│    │ Service │  │ Service  │  │  Service     │  │
│    └─────────┘  └──────────┘  └──────────────┘  │
│                                                  │
│         SignalR Hub (Real-time Updates)          │
│         Stripe (Premium Features)                │
└──────────────────────────────────────────────────┘
```

## Implementation Steps

### Phase 1: Member Portal (Weeks 1-3)

#### 1.1 Project Setup
- [ ] Clone portal structure:
  ```bash
  cp -r portal/CloudHealthOffice.Portal portal/CloudHealthOffice.MemberPortal
  cd portal/CloudHealthOffice.MemberPortal
  dotnet new sln -n MemberPortal
  ```

- [ ] Update project references:
  ```xml
  <!-- MemberPortal.csproj -->
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="8.0.0" />
    <PackageReference Include="MudBlazor" Version="6.11.0" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="8.0.0" />
    <PackageReference Include="Microsoft.Identity.Web" Version="2.15.0" />
    <PackageReference Include="Microsoft.Identity.Web.UI" Version="2.15.0" />
  </ItemGroup>
  ```

#### 1.2 Azure AD B2C Configuration
- [ ] Create Azure AD B2C tenant: `cloudhealthofficemembers.onmicrosoft.com`
- [ ] Configure user flows:
  - Sign-up/Sign-in (SUSI): Collect email, first/last name, member ID
  - Password reset
  - Profile editing
- [ ] Register MemberPortal app in Azure AD B2C
- [ ] Add custom attributes:
  - `extension_MemberId` (link to Member Service)
  - `extension_TenantId` (for multi-tenant isolation)
  - `extension_SubscriptionTier` (free/premium)

- [ ] Update `appsettings.json`:
  ```json
  {
    "AzureAdB2C": {
      "Instance": "https://cloudhealthofficemembers.b2clogin.com/",
      "ClientId": "<member-portal-client-id>",
      "Domain": "cloudhealthofficemembers.onmicrosoft.com",
      "SignUpSignInPolicyId": "B2C_1_susi",
      "CallbackPath": "/signin-oidc",
      "Scopes": "openid profile email"
    }
  }
  ```

#### 1.3 Member Portal Pages

##### Home/Dashboard
```razor
@page "/"
@attribute [Authorize]

<MudText Typo="Typo.h4">Welcome, @context.User.Identity.Name</MudText>

<MudGrid>
    <MudItem xs="12" md="4">
        <MudCard>
            <MudCardHeader>
                <CardHeaderContent>
                    <MudText Typo="Typo.h6">Coverage Summary</MudText>
                </CardHeaderContent>
            </MudCardHeader>
            <MudCardContent>
                <MudText>Plan: @coverage.PlanName</MudText>
                <MudText>Status: <MudChip Color="Color.Success">Active</MudChip></MudText>
                <MudText>Deductible: $@coverage.DeductibleMet / $@coverage.DeductibleTotal</MudText>
            </MudCardContent>
        </MudCard>
    </MudItem>
    
    <MudItem xs="12" md="4">
        <MudCard>
            <MudCardHeader>
                <CardHeaderContent>
                    <MudText Typo="Typo.h6">Recent Claims</MudText>
                </CardHeaderContent>
            </MudCardHeader>
            <MudCardContent>
                @foreach (var claim in recentClaims)
                {
                    <MudText>@claim.ServiceDate.ToShortDateString(): @claim.Status</MudText>
                }
            </MudCardContent>
        </MudCard>
    </MudItem>
</MudGrid>
```

##### Eligibility Check (`Pages/Eligibility.razor`)
- [ ] Real-time 270/271 transaction via Eligibility Service
- [ ] Form: Service type, provider NPI, service date
- [ ] Display: Coverage status, copay, deductible, out-of-pocket max
- [ ] Export to PDF

##### Benefits View (`Pages/Benefits.razor`)
- [ ] Display plan benefits from Coverage Service
- [ ] Categories: Medical, dental, vision, pharmacy, mental health
- [ ] Show copays, coinsurance, network details
- [ ] Link to provider directory

##### Prior Authorization (`Pages/PriorAuth.razor`)
- [ ] Submit 278 (authorization request)
- [ ] Track authorization status (pending/approved/denied)
- [ ] Upload supporting documents (e.g., medical records)
- [ ] Real-time status updates via SignalR

##### Claims History (`Pages/Claims.razor`)
- [ ] List all claims from Claims Service (filtered by member)
- [ ] Filters: Date range, status, provider
- [ ] Details: Service date, provider, billed amount, paid amount, patient responsibility
- [ ] Explanation of Benefits (EOB) download

#### 1.4 SignalR Real-Time Updates
- [ ] Create SignalR hub in Claims Service:
  ```csharp
  // services/claims-service/Hubs/ClaimStatusHub.cs
  public class ClaimStatusHub : Hub
  {
      public async Task SubscribeToClaimUpdates(string memberId)
      {
          await Groups.AddToGroupAsync(Context.ConnectionId, $"member-{memberId}");
      }
      
      public async Task NotifyClaimUpdate(string memberId, ClaimStatusUpdate update)
      {
          await Clients.Group($"member-{memberId}").SendAsync("ClaimUpdated", update);
      }
  }
  ```

- [ ] Connect in Member Portal:
  ```csharp
  // Services/ClaimNotificationService.cs
  private HubConnection _hubConnection;
  
  public async Task StartAsync()
  {
      _hubConnection = new HubConnectionBuilder()
          .WithUrl("https://api.cloudhealthoffice.com/hubs/claimstatus")
          .Build();
      
      _hubConnection.On<ClaimStatusUpdate>("ClaimUpdated", update =>
      {
          NotificationService.Notify($"Claim {update.ClaimId} status: {update.NewStatus}");
      });
      
      await _hubConnection.StartAsync();
      await _hubConnection.InvokeAsync("SubscribeToClaimUpdates", _memberId);
  }
  ```

### Phase 2: Provider Portal (Weeks 3-5)

#### 2.1 Project Setup
- [ ] Clone and configure similar to Member Portal
- [ ] Azure AD B2C tenant: `cloudhealthofficeproviders.onmicrosoft.com`
- [ ] Custom attributes: `extension_NPI`, `extension_TaxonomyCode`, `extension_PracticeGroupId`

#### 2.2 Provider Portal Pages

##### Claims Submission (`Pages/SubmitClaim.razor`)
- [ ] Multi-step wizard:
  1. Patient info (member ID, name, DOB)
  2. Service details (CPT codes, diagnosis codes, dates)
  3. Provider info (NPI, taxonomy, place of service)
  4. Review and submit
- [ ] Generate 837 Professional or Institutional
- [ ] Upload via Clearinghouse Adapter Service
- [ ] Real-time validation (e.g., invalid CPT code)

##### Provider Directory (`Pages/Directory.razor`)
- [ ] Search providers by name, NPI, specialty, location
- [ ] Display: Contact info, accepted plans, performance rating
- [ ] Invite to network (for admin users)

##### Performance Dashboard (`Pages/Performance.razor`)
- [ ] Metrics from Provider Service:
  - Claims volume (90-day trend)
  - Authorization approval rate
  - Avg time to payment
  - Denial rate by reason code
  - Quality score (star rating)
- [ ] Interactive charts (ApexCharts.NET)
- [ ] Export to PDF/Excel

##### Remittance Viewer (`Pages/Remittances.razor`)
- [ ] Display 835 remittance advice
- [ ] Group by payment batch
- [ ] Show adjustments (contractual, deductible, copay)
- [ ] Download ERA (Electronic Remittance Advice)

### Phase 3: Stripe Premium Features (Week 5)

#### 3.1 Feature Gating
- [ ] Add `SubscriptionTier` to user claims (from Azure AD B2C)
- [ ] Create authorization policy:
  ```csharp
  builder.Services.AddAuthorization(options =>
  {
      options.AddPolicy("PremiumFeatures", policy =>
          policy.RequireClaim("extension_SubscriptionTier", "professional", "enterprise"));
  });
  ```

- [ ] Gate advanced reports:
  ```razor
  @attribute [Authorize(Policy = "PremiumFeatures")]
  
  @code {
      // Premium analytics page
  }
  ```

#### 3.2 Stripe Checkout Integration
- [ ] Add upgrade prompt for free users:
  ```razor
  @if (!IsPremium)
  {
      <MudAlert Severity="Severity.Info">
          Unlock advanced analytics with a Premium subscription.
          <MudButton Href="/subscribe">Upgrade Now</MudButton>
      </MudAlert>
  }
  ```

- [ ] Create checkout session:
  ```csharp
  // Pages/Subscribe.razor.cs
  public async Task<IActionResult> CreateCheckoutSession()
  {
      var session = await _stripeService.CreateCheckoutSessionAsync(new CreateCheckoutSessionRequest
      {
          SuccessUrl = "https://portal.cloudhealthoffice.com/payment-success",
          CancelUrl = "https://portal.cloudhealthoffice.com/pricing",
          CustomerId = User.FindFirst("extension_StripeCustomerId")?.Value,
          PriceId = "price_professional_monthly"
      });
      
      return Redirect(session.Url);
  }
  ```

- [ ] Handle webhook to update Azure AD B2C custom attribute:
  ```csharp
  // In Stripe webhook handler
  case "checkout.session.completed":
      var customerId = session.CustomerId;
      var memberId = session.Metadata["member_id"];
      await _graphClient.UpdateUserAsync(memberId, new
      {
          extension_SubscriptionTier = "professional",
          extension_StripeCustomerId = customerId
      });
      break;
  ```

### Phase 4: Mobile Responsiveness (Week 6)

- [ ] Test on mobile devices (iOS Safari, Android Chrome)
- [ ] Optimize MudBlazor components for touch:
  ```razor
  <MudDrawer @bind-Open="_drawerOpen" Variant="DrawerVariant.Responsive">
      <!-- Collapsible nav for mobile -->
  </MudDrawer>
  ```
- [ ] Add PWA manifest for "Add to Home Screen":
  ```json
  {
    "name": "Cloud Health Office Member Portal",
    "short_name": "CHO Member",
    "start_url": "/",
    "display": "standalone",
    "icons": [
      {
        "src": "images/logo-192x192.png",
        "sizes": "192x192",
        "type": "image/png"
      }
    ]
  }
  ```

### Phase 5: FHIR R4 Integration (Week 7)

- [ ] Add FHIR resource endpoints to Member/Provider services:
  ```csharp
  // GET /fhir/Patient/{id}
  public async Task<Patient> GetPatientAsync(string id)
  {
      var member = await _memberService.GetMemberAsync(id);
      return new Patient
      {
          Id = member.MemberId,
          Name = new List<HumanName> { new HumanName { Family = member.LastName, Given = new[] { member.FirstName } } },
          BirthDate = member.DateOfBirth.ToString("yyyy-MM-dd"),
          Gender = member.Gender == "M" ? AdministrativeGender.Male : AdministrativeGender.Female
      };
  }
  ```

- [ ] Expose FHIR-compliant endpoints:
  - `Patient` (from Member Service)
  - `Coverage` (from Coverage Service)
  - `Claim` (from Claims Service)
  - `ExplanationOfBenefit` (from Claims Service)

## Tech Stack
- **Frontend:** Blazor Server (.NET 8), MudBlazor 6.11, ApexCharts.NET
- **Authentication:** Azure AD B2C, Microsoft.Identity.Web
- **Real-time:** SignalR (ASP.NET Core)
- **Payments:** Stripe.js, Stripe Checkout
- **API:** RESTful microservices (Member, Coverage, Claims, Provider)
- **Standards:** FHIR R4 (HL7)

## Testing

### UI Tests (Playwright)
```csharp
[Test]
public async Task MemberPortal_EligibilityCheck_ShowsCoverageStatus()
{
    await Page.GotoAsync("https://members.cloudhealthoffice.com/eligibility");
    await Page.FillAsync("#member-id", "M123456789");
    await Page.ClickAsync("button:has-text('Check Eligibility')");
    await Expect(Page.Locator(".coverage-status")).ToHaveTextAsync("Active");
}
```

### E2E Tests
- [ ] Member login → view benefits → check eligibility → submit prior auth
- [ ] Provider login → submit claim → view remittance → download ERA
- [ ] Free user tries premium feature → redirected to upgrade page → completes Stripe checkout → gains access

### Accessibility (a11y)
- [ ] WCAG 2.1 AA compliance (contrast ratios, keyboard navigation)
- [ ] Screen reader testing (NVDA, VoiceOver)
- [ ] Automated scans with axe-core

## Dependencies
- ✅ Tenant Management Service (user-to-tenant mapping)
- ✅ Stripe Billing (premium subscriptions)
- ⏳ Azure AD B2C setup
- ⏳ Member/Provider services API readiness

## Documentation
- [ ] Create [docs/MEMBER-PORTAL.md](../../docs/MEMBER-PORTAL.md) user guide
- [ ] Create [docs/PROVIDER-PORTAL.md](../../docs/PROVIDER-PORTAL.md) user guide
- [ ] Update [ARCHITECTURE.md](../../ARCHITECTURE.md) with portal diagram
- [ ] Add Swagger docs for FHIR endpoints

## Success Criteria
- ✅ Member portal: 100% feature parity with requirements
- ✅ Provider portal: Claims submission success rate >95%
- ✅ SignalR notifications: <5s latency for claim updates
- ✅ Mobile responsive: All pages render correctly on iPhone/Android
- ✅ Premium conversion: >10% of free users upgrade within 30 days
- ✅ Accessibility: WCAG 2.1 AA passing (0 critical issues)

## Timeline
- **Weeks 1-3:** Member Portal (pages, auth, SignalR)
- **Weeks 3-5:** Provider Portal (claims submission, performance dashboard)
- **Week 5:** Stripe premium features
- **Week 6:** Mobile responsiveness + PWA
- **Week 7:** FHIR R4 integration

**Total:** 7 weeks (2 FTE)

## References
- [MudBlazor Documentation](https://mudblazor.com/)
- [Azure AD B2C Docs](https://learn.microsoft.com/en-us/azure/active-directory-b2c/)
- [FHIR R4 Specification](https://hl7.org/fhir/R4/)
- [Stripe Checkout](https://stripe.com/docs/payments/checkout)
