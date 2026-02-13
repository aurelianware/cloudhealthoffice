---
name: "[v4.0] Mobile Apps"
about: Develop cross-platform iOS/Android apps for member and provider access
title: "[v4.0] Develop Mobile Apps for Member and Provider Access"
labels: enhancement, mobile, v4.0, priority-low
assignees: aurelianware
---

## Overview
Create cross-platform mobile apps (iOS/Android) for on-the-go access to member and provider portals. Focus on core features like eligibility checks and claims tracking, integrating tenant management for secure authentication and Stripe for in-app purchases.

## Objectives
- ✅ Native mobile experience for members and providers
- ✅ Core features: Eligibility, claims, prior auth, provider directory
- ✅ Push notifications for claim status updates
- ✅ Offline mode with local caching
- ✅ Stripe in-app purchases for premium features
- ✅ App Store and Google Play deployment

## Tech Stack Decision

### Option 1: .NET MAUI (Recommended)
**Pros:**
- Leverage existing .NET codebase (90% code reuse)
- Single codebase for iOS/Android/Windows
- Native performance
- Direct integration with existing microservices
- Team already familiar with C#/.NET

**Cons:**
- Newer framework (some rough edges)
- Limited third-party component library vs React Native

### Option 2: React Native
**Pros:**
- Mature ecosystem
- Large component library
- Hot reload for fast development

**Cons:**
- Requires JavaScript expertise
- Less code reuse with .NET backend
- Bridge performance overhead

**Decision:** Use .NET MAUI for maximum code reuse and native performance

## Architecture

```
┌─────────────────────────────────────────────────┐
│           Cloud Health Office Mobile            │
├─────────────────────────────────────────────────┤
│                                                 │
│  ┌─────────────┐          ┌─────────────┐      │
│  │   Member    │          │  Provider   │      │
│  │     App     │          │     App     │      │
│  │  (iOS/And)  │          │  (iOS/And)  │      │
│  └──────┬──────┘          └──────┬──────┘      │
│         │                         │             │
│         └─────────────┬───────────┘             │
│                       │                         │
│              .NET MAUI Framework                │
│                       │                         │
│         ┌─────────────┴─────────────┐           │
│         │                           │           │
│    ┌────▼────┐  ┌────────┐  ┌──────▼──────┐    │
│    │   API   │  │ Azure  │  │   SignalR   │    │
│    │ Client  │  │ AD B2C │  │   (Push)    │    │
│    └─────────┘  └────────┘  └─────────────┘    │
│                                                 │
│         Secure Storage (Keychain/Keystore)      │
│         Local Cache (SQLite)                    │
│         Stripe Mobile SDK                       │
└─────────────────────────────────────────────────┘
```

## Implementation Steps

### Phase 1: Project Setup (Week 1)

#### 1.1 Create MAUI Projects

```bash
# Create solution
dotnet new sln -n CloudHealthOfficeMobile

# Create member app
dotnet new maui -n CloudHealthOffice.MemberApp
dotnet sln add CloudHealthOffice.MemberApp/CloudHealthOffice.MemberApp.csproj

# Create provider app
dotnet new maui -n CloudHealthOffice.ProviderApp
dotnet sln add CloudHealthOffice.ProviderApp/CloudHealthOffice.ProviderApp.csproj

# Create shared library
dotnet new classlib -n CloudHealthOffice.Mobile.Shared
dotnet sln add CloudHealthOffice.Mobile.Shared/CloudHealthOffice.Mobile.Shared.csproj
```

**Project Structure:**
```
mobile/
├── CloudHealthOffice.MemberApp/
│   ├── Platforms/
│   │   ├── Android/
│   │   └── iOS/
│   ├── Pages/
│   │   ├── EligibilityPage.xaml
│   │   ├── ClaimsPage.xaml
│   │   └── PriorAuthPage.xaml
│   ├── ViewModels/
│   └── Services/
├── CloudHealthOffice.ProviderApp/
│   ├── Pages/
│   │   ├── ClaimSubmissionPage.xaml
│   │   ├── DirectoryPage.xaml
│   │   └── PerformancePage.xaml
│   └── ViewModels/
└── CloudHealthOffice.Mobile.Shared/
    ├── Models/ (Claim, Member, Coverage - reuse from services)
    ├── Services/
    │   ├── IApiClient.cs
    │   ├── ApiClient.cs
    │   ├── IAuthService.cs
    │   └── AuthService.cs
    └── Helpers/
        ├── SecureStorage.cs
        └── CacheManager.cs
```

#### 1.2 Install Packages

```xml
<!-- CloudHealthOffice.MemberApp.csproj -->
<ItemGroup>
  <PackageReference Include="Microsoft.Identity.Client" Version="4.57.0" />
  <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="8.0.0" />
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
  <PackageReference Include="CommunityToolkit.Maui" Version="7.0.0" />
  <PackageReference Include="Stripe.net" Version="44.0.0" />
  <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  <PackageReference Include="sqlite-net-pcl" Version="1.8.116" />
</ItemGroup>
```

### Phase 2: Authentication (Week 1-2)

#### 2.1 Azure AD B2C Integration

```csharp
// CloudHealthOffice.Mobile.Shared/Services/AuthService.cs
using Microsoft.Identity.Client;

public class AuthService : IAuthService
{
    private readonly IPublicClientApplication _pca;
    
    public AuthService()
    {
        _pca = PublicClientApplicationBuilder
            .Create("member-app-client-id")
            .WithB2CAuthority("https://cloudhealthofficemembers.b2clogin.com/tfp/cloudhealthofficemembers.onmicrosoft.com/B2C_1_susi")
            .WithRedirectUri("msal{client-id}://auth")
            .WithIosKeychainSecurityGroup("com.cloudhealthoffice.memberapp")
            .Build();
    }
    
    public async Task<AuthenticationResult> SignInAsync()
    {
        try
        {
            // Try silent sign-in first
            var accounts = await _pca.GetAccountsAsync();
            return await _pca.AcquireTokenSilent(new[] { "openid", "profile" }, accounts.FirstOrDefault())
                .ExecuteAsync();
        }
        catch (MsalUiRequiredException)
        {
            // Interactive sign-in required
            return await _pca.AcquireTokenInteractive(new[] { "openid", "profile" })
                .WithParentActivityOrWindow(GetParentWindow())
                .ExecuteAsync();
        }
    }
    
    public async Task SignOutAsync()
    {
        var accounts = await _pca.GetAccountsAsync();
        foreach (var account in accounts)
        {
            await _pca.RemoveAsync(account);
        }
        
        // Clear secure storage
        SecureStorage.RemoveAll();
    }
}
```

#### 2.2 iOS Configuration

```xml
<!-- Platforms/iOS/Info.plist -->
<key>CFBundleURLTypes</key>
<array>
  <dict>
    <key>CFBundleURLSchemes</key>
    <array>
      <string>msal{client-id}</string>
    </array>
  </dict>
</array>
```

#### 2.3 Android Configuration

```xml
<!-- Platforms/Android/AndroidManifest.xml -->
<activity android:name="microsoft.identity.client.BrowserTabActivity">
  <intent-filter>
    <action android:name="android.intent.action.VIEW" />
    <category android:name="android.intent.category.DEFAULT" />
    <category android:name="android.intent.category.BROWSABLE" />
    <data android:scheme="msal{client-id}"
          android:host="auth" />
  </intent-filter>
</activity>
```

### Phase 3: Member App Features (Weeks 2-4)

#### 3.1 Eligibility Check Page

```xml
<!-- Pages/EligibilityPage.xaml -->
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             Title="Check Eligibility">
    <ScrollView>
        <VerticalStackLayout Padding="20" Spacing="15">
            <Label Text="Member ID" />
            <Entry Text="{Binding MemberId}" Placeholder="M123456789" />
            
            <Label Text="Service Type" />
            <Picker ItemsSource="{Binding ServiceTypes}" 
                    SelectedItem="{Binding SelectedServiceType}" />
            
            <Label Text="Provider NPI" />
            <Entry Text="{Binding ProviderNpi}" Keyboard="Numeric" />
            
            <Button Text="Check Eligibility" 
                    Command="{Binding CheckEligibilityCommand}"
                    IsEnabled="{Binding IsNotBusy}" />
            
            <Frame IsVisible="{Binding HasResult}">
                <VerticalStackLayout>
                    <Label Text="Coverage Status" FontAttributes="Bold" />
                    <Label Text="{Binding CoverageStatus}" 
                           TextColor="{Binding StatusColor}" />
                    
                    <Label Text="Copay" FontAttributes="Bold" Margin="0,10,0,0" />
                    <Label Text="{Binding Copay, StringFormat='${0:N2}'}" />
                    
                    <Label Text="Deductible Remaining" FontAttributes="Bold" Margin="0,10,0,0" />
                    <Label Text="{Binding DeductibleRemaining, StringFormat='${0:N2}'}" />
                </VerticalStackLayout>
            </Frame>
        </VerticalStackLayout>
    </ScrollView>
</ContentPage>
```

```csharp
// ViewModels/EligibilityViewModel.cs
public partial class EligibilityViewModel : ObservableObject
{
    private readonly IApiClient _apiClient;
    
    [ObservableProperty]
    private string memberId;
    
    [ObservableProperty]
    private string providerNpi;
    
    [ObservableProperty]
    private bool hasResult;
    
    [RelayCommand]
    private async Task CheckEligibility()
    {
        IsBusy = true;
        try
        {
            var result = await _apiClient.PostAsync<EligibilityResponse>(
                "/api/v1/eligibility/check",
                new EligibilityRequest
                {
                    MemberId = MemberId,
                    ServiceType = SelectedServiceType,
                    ProviderNpi = ProviderNpi
                });
            
            CoverageStatus = result.Status;
            Copay = result.Copay;
            DeductibleRemaining = result.DeductibleRemaining;
            HasResult = true;
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", ex.Message, "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
```

#### 3.2 Claims History Page

```xml
<!-- Pages/ClaimsPage.xaml -->
<ContentPage Title="Claims">
    <CollectionView ItemsSource="{Binding Claims}">
        <CollectionView.ItemTemplate>
            <DataTemplate>
                <SwipeView>
                    <SwipeView.RightItems>
                        <SwipeItems>
                            <SwipeItem Text="Details" 
                                      BackgroundColor="LightBlue"
                                      Command="{Binding Source={RelativeSource AncestorType={x:Type local:ClaimsViewModel}}, Path=ViewDetailsCommand}"
                                      CommandParameter="{Binding .}" />
                        </SwipeItems>
                    </SwipeView.RightItems>
                    
                    <Grid Padding="15">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="Auto" />
                        </Grid.RowDefinitions>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        
                        <Label Text="{Binding ProviderName}" FontAttributes="Bold" />
                        <Label Grid.Row="1" Text="{Binding ServiceDate, StringFormat='{0:MMM dd, yyyy}'}" 
                               TextColor="Gray" FontSize="12" />
                        
                        <Label Grid.Column="1" Text="{Binding Status}" 
                               TextColor="{Binding StatusColor}" />
                        <Label Grid.Row="1" Grid.Column="1" 
                               Text="{Binding PatientResponsibility, StringFormat='You owe: ${0:N2}'}" 
                               FontSize="12" />
                    </Grid>
                </SwipeView>
            </DataTemplate>
        </CollectionView.ItemTemplate>
    </CollectionView>
</ContentPage>
```

#### 3.3 Push Notifications

```csharp
// Services/NotificationService.cs
using Microsoft.AspNetCore.SignalR.Client;

public class NotificationService
{
    private HubConnection _hubConnection;
    
    public async Task StartAsync(string memberId)
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl("https://api.cloudhealthoffice.com/hubs/claimstatus", options =>
            {
                options.AccessTokenProvider = async () => await GetAccessTokenAsync();
            })
            .WithAutomaticReconnect()
            .Build();
        
        _hubConnection.On<ClaimStatusUpdate>("ClaimUpdated", async (update) =>
        {
            // Show local notification
            await LocalNotificationCenter.Current.Show(new NotificationRequest
            {
                NotificationId = 1,
                Title = "Claim Update",
                Description = $"Claim {update.ClaimId} is now {update.NewStatus}",
                BadgeNumber = 1
            });
        });
        
        await _hubConnection.StartAsync();
        await _hubConnection.InvokeAsync("SubscribeToClaimUpdates", memberId);
    }
}
```

### Phase 4: Provider App Features (Weeks 4-5)

#### 4.1 Claim Submission Page

```xml
<!-- Pages/ClaimSubmissionPage.xaml -->
<ContentPage Title="Submit Claim">
    <ScrollView>
        <VerticalStackLayout Padding="20">
            <!-- Step 1: Patient Info -->
            <Frame>
                <VerticalStackLayout>
                    <Label Text="Patient Information" FontSize="18" FontAttributes="Bold" />
                    <Entry Placeholder="Member ID" Text="{Binding MemberId}" />
                    <Entry Placeholder="First Name" Text="{Binding FirstName}" />
                    <Entry Placeholder="Last Name" Text="{Binding LastName}" />
                    <DatePicker Date="{Binding DateOfBirth}" />
                </VerticalStackLayout>
            </Frame>
            
            <!-- Step 2: Service Details -->
            <Frame>
                <VerticalStackLayout>
                    <Label Text="Service Details" FontSize="18" FontAttributes="Bold" />
                    <CollectionView ItemsSource="{Binding ServiceLines}">
                        <!-- CPT codes, charges, diagnosis codes -->
                    </CollectionView>
                    <Button Text="Add Service Line" Command="{Binding AddServiceLineCommand}" />
                </VerticalStackLayout>
            </Frame>
            
            <!-- Submit -->
            <Button Text="Submit Claim" 
                    Command="{Binding SubmitClaimCommand}"
                    BackgroundColor="Green" />
        </VerticalStackLayout>
    </ScrollView>
</ContentPage>
```

### Phase 5: Offline Mode (Week 5-6)

#### 5.1 Local Caching with SQLite

```csharp
// Services/CacheManager.cs
using SQLite;

public class CacheManager
{
    private readonly SQLiteAsyncConnection _db;
    
    public async Task<List<Claim>> GetCachedClaimsAsync(string memberId)
    {
        return await _db.Table<Claim>()
            .Where(c => c.MemberId == memberId)
            .OrderByDescending(c => c.ServiceDate)
            .ToListAsync();
    }
    
    public async Task CacheClaimsAsync(List<Claim> claims)
    {
        await _db.InsertAllAsync(claims, "OR REPLACE");
    }
    
    public async Task SyncWhenOnlineAsync()
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            return;
        
        var pendingClaims = await _db.Table<Claim>()
            .Where(c => c.IsPendingSync)
            .ToListAsync();
        
        foreach (var claim in pendingClaims)
        {
            try
            {
                await _apiClient.PostAsync("/api/v1/claims", claim);
                claim.IsPendingSync = false;
                await _db.UpdateAsync(claim);
            }
            catch
            {
                // Retry later
            }
        }
    }
}
```

### Phase 6: Stripe In-App Purchases (Week 6)

```csharp
// Services/SubscriptionService.cs
public class SubscriptionService
{
    public async Task UpgradeToPremiumAsync()
    {
        // iOS: Use StoreKit
        // Android: Use Google Play Billing
        
        #if IOS
        var product = await GetProductAsync("com.cloudhealthoffice.premium");
        var payment = SKPayment.PaymentWithProduct(product);
        SKPaymentQueue.DefaultQueue.AddPayment(payment);
        #elif ANDROID
        var billingClient = BillingClient.NewBuilder(context)
            .SetListener(this)
            .EnablePendingPurchases()
            .Build();
        // Launch billing flow
        #endif
    }
}
```

### Phase 7: App Store Deployment (Week 7)

#### 7.1 iOS App Store

```bash
# Build release
dotnet build -c Release -f net8.0-ios

# Archive
dotnet publish -f net8.0-ios -c Release -p:ArchiveOnBuild=true

# Upload to App Store Connect via Xcode or Transporter
```

**App Store Listing:**
- Name: Cloud Health Office - Member Portal
- Category: Medical
- Screenshots: iPhone 15 Pro, iPad Pro
- Description: "Manage your health benefits on the go..."

#### 7.2 Google Play Store

```bash
# Build Android App Bundle
dotnet publish -c Release -f net8.0-android -p:AndroidPackageFormat=aab

# Sign with release keystore
jarsigner -verbose -sigalg SHA256withRSA -digestalg SHA-256 \
  -keystore release.keystore app.aab alias_name
```

**Play Store Listing:**
- Name: Cloud Health Office - Member Portal
- Category: Medical
- Content Rating: Everyone
- Privacy Policy: https://cloudhealthoffice.com/privacy

## Testing

### Unit Tests
```csharp
[Fact]
public async Task AuthService_SignIn_ReturnsValidToken()
{
    var auth = new AuthService();
    var result = await auth.SignInAsync();
    Assert.NotNull(result.AccessToken);
}
```

### UI Tests (Appium)
```csharp
[Test]
public void EligibilityPage_CheckEligibility_ShowsResult()
{
    driver.FindElement(By.Id("member-id")).SendKeys("M123456789");
    driver.FindElement(By.Id("check-button")).Click();
    
    var status = driver.FindElement(By.Id("coverage-status")).Text;
    Assert.Equal("Active", status);
}
```

### Device Testing
- iOS: iPhone 12, 13, 14, 15 (simulators + physical)
- Android: Pixel 6, Samsung S23, OnePlus (emulators + physical)

## Dependencies
- ✅ Member/Provider portals (feature parity)
- ✅ Tenant management (auth context)
- ✅ Stripe billing (in-app purchases)
- ⏳ Azure AD B2C mobile app registration
- ⏳ Apple Developer account ($99/year)
- ⏳ Google Play Console account ($25 one-time)

## Documentation
- [ ] Create [docs/MOBILE-APPS.md](../../docs/MOBILE-APPS.md)
- [ ] App Store / Play Store privacy policy
- [ ] User guides (screenshots, videos)

## Success Criteria
- ✅ Apps approved on App Store and Play Store
- ✅ 4.5+ star rating (post-launch)
- ✅ Offline mode works for 7 days
- ✅ Push notifications <10s latency
- ✅ Crash rate <1%

## Timeline
- **Week 1:** Project setup + auth
- **Weeks 2-4:** Member app features
- **Weeks 4-5:** Provider app features
- **Weeks 5-6:** Offline mode + Stripe
- **Week 7:** App Store deployment

**Total:** 7 weeks (2 FTE)

## References
- [.NET MAUI Documentation](https://learn.microsoft.com/en-us/dotnet/maui/)
- [MSAL for Mobile](https://learn.microsoft.com/en-us/azure/active-directory/develop/msal-overview)
- [App Store Guidelines](https://developer.apple.com/app-store/review/guidelines/)
- [Google Play Policies](https://play.google.com/about/developer-content-policy/)
