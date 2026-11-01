# Security awareness training: hosted environment

**Status:** DRAFT. Every workforce member completes this before getting access, and again each year.
**Rule:** 45 CFR 164.308(a)(5): security reminders, protection from malicious software, log-in monitoring, password management.

## Content (about 45 minutes)

1. **What PHI is and where it lives.** The asset table in the [risk analysis](RISK-ANALYSIS.md). If a file might be PHI, treat it as PHI.
2. **The hard rules** ([data handling](../../diligence/DATA-HANDLING.md) and [POLICIES.md](POLICIES.md), section 6):
   - no PHI in GitHub, tickets, chat, email, screenshots or laptops
   - no shared accounts
   - access only for a reason the plan raised
3. **Signing in.** 2-step verification with a security key or authenticator app. Never approve a prompt you didn't start. Never reuse passwords, and use a password manager.
4. **Phishing and malware.** Check the sender and link before signing in anywhere. Report anything suspicious. Keep the OS and browser updated. Don't install unreviewed tools on admin laptops.
5. **Log-in monitoring.** Alerts on IAM and key changes go to the security list. If you get an alert for something you didn't do, report it at once.
6. **Reporting an incident.** What counts and how to report ([INCIDENT-RESPONSE.md](INCIDENT-RESPONSE.md)). Report quickly: the plan's notice deadline starts at discovery.
7. **Sanctions.** What happens when the rules are broken ([POLICIES.md](POLICIES.md), section 2).
8. **AI tools.** Never paste PHI into an AI tool or chat unless a BAA covers it and the deploy is configured for it. The AI claims examiner runs only with a BAA in place.

Reminders go out each quarter with the access review: one short email on a current topic.

## Device checklist (confirm before access)

- [ ] Full-disk encryption on (FileVault, BitLocker or LUKS)
- [ ] Screen locks after five minutes or less
- [ ] Automatic OS and browser updates on
- [ ] 2-step verification on the Google account, with a security key or authenticator app
- [ ] No PHI stored on this device

## Record

Kept in the private compliance store for six years. One row per person per session:

| Name | Role | Date | Content version | Device checklist done | I have read and will follow the policies (signature) |
| --- | --- | --- | --- | --- | --- |
| | | | | | |
| | | | | | |
