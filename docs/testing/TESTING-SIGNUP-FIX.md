# Testing the Fixed Signup Form

## What Was Fixed

### Problem
- Stripe.js was being loaded multiple times causing browser warnings
- JavaScript `eval()` calls were failing for async Stripe functions
- Form submission wasn't reaching the server
- Blazor SignalR circuit was getting disconnected

### Solution
1. **Moved Stripe.js to _Host.cshtml** - Now loads once globally instead of per-component
2. **Created stripe-handler.js** - Proper JavaScript module with named functions
3. **Fixed JS Interop** - Replaced `eval()` calls with `JSRuntime.InvokeAsync` to named functions
4. **Added Logging** - Console logs and server logs for debugging

## How to Test

### 1. Clear Browser Cache
Before testing, **clear your browser cache** or use incognito/private mode to ensure you get the new JavaScript files:

```bash
# Chrome: Cmd+Shift+Delete → Clear browsing data
# Or use incognito: Cmd+Shift+N
```

### 2. Open Developer Console
1. Navigate to your local or customer-deployed portal signup page, for example `http://localhost:5026/signup` or `https://portal.<your-domain>/signup`
2. Open browser console (F12 or Cmd+Option+I)
3. Check the **Console** tab for debug messages

### 3. Expected Console Output
You should see these messages in order:
```
Initializing Stripe with key: pk_test...
Card element mounted successfully
```

You should **NOT** see:
- ❌ "Stripe.js loaded more than once"
- ❌ WebSocket connection errors
- ❌ "Card element div not found"

### 4. Fill Out the Form
Use the Stripe test card:
- **Card Number**: `4242 4242 4242 4242`
- **Expiry**: Any future date (e.g., `12/28`)
- **CVC**: Any 3 digits (e.g., `123`)
- **Name**: Any name (e.g., `John Doe`)
- **Email**: Any email (e.g., `test@example.com`)
- **Organization**: Any name (e.g., `Test Hospital`)

### 5. Click "Start Free Trial"
Watch the console output. You should see:
```
Creating payment method for: John Doe test@example.com
Payment method created: pm_xxxxxxxxxxxxx
```

The button should show "Processing..." while working.

### 6. Check Server Logs
In a terminal, watch for signup events:

```bash
kubectl logs -n cloudhealthoffice -l app=portal -f | grep -i "signup\|payment\|stripe"
```

You should see logs like:
```
Starting signup for user test@example.com from Azure Tenant xxxxx
Creating Stripe payment method for John Doe (test@example.com)
Payment method created successfully: pm_xxxxx
Would create Stripe subscription for tier starter with payment method pm_xxxxx
Successfully created tenant tenant-xxxxx for organization Test Hospital
```

## Debugging

### If the button still doesn't work:

1. **Check Console Errors**
   - Look for any red errors in the console
   - Screenshot and share any error messages

2. **Verify Stripe Loaded**
   - In the console, type: `typeof Stripe`
   - Should return: `"function"`
   - If it returns `"undefined"`, Stripe.js didn't load

3. **Check StripeHandler**
   - In the console, type: `typeof StripeHandler`
   - Should return: `"object"`
   - If undefined, stripe-handler.js didn't load

4. **Test Stripe Initialization**
   - In the console, type: `StripeHandler.stripe`
   - Should return: `Stripe {...}` object
   - If null, initialization failed

5. **Verify Card Element**
   - In the console, type: `StripeHandler.cardElement`
   - Should return: `StripeElement {...}` object
   - If null, card element wasn't mounted

### Common Issues

**Issue**: "Payment system not initialized" error
- **Cause**: StripeHandler.initialize wasn't called or failed
- **Fix**: Check console for initialization errors

**Issue**: Form submits but no server logs
- **Cause**: C# HandleSubmit method throwing exception
- **Fix**: Check browser console for Blazor errors

**Issue**: Card element doesn't appear
- **Cause**: Stripe not loaded before InitializeStripe runs
- **Fix**: Refresh page, check Network tab for stripe-handler.js (should be 200)

## Build Info
- Commit: `6497cd4`
- Build: Completed successfully at 2026-02-09T05:47:24Z
- Deployed: Pods restarted and running
- Files: stripe-handler.js verified accessible (HTTP 200)

## Next Steps After Successful Test

Once you confirm the signup form works:

1. **Create Real Stripe Subscription** - Implement backend API to create actual Stripe customer and subscription
2. **Save to Cosmos DB** - Implement TenantService.CreateTenantAsync to persist tenant record
3. **Trigger Argo Workflow** - Implement TriggerOnboardingWorkflow to kick off automated provisioning
4. **Email Confirmation** - Send welcome email with trial details
5. **Redirect to Dashboard** - Send user to /dashboard after successful signup

## Questions?

If you still have issues:
1. Screenshot the browser console
2. Share any error messages
3. Run the kubectl logs command and share output
