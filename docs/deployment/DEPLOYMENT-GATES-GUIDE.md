# Deployment Gates and Approval Strategy Guide

## Overview

This guide provides comprehensive information about the gated release strategy implemented for Cloud Health Office deployments to UAT and PROD environments.

## Table of Contents

- [Quick Start](#quick-start)
- [Approval Gate Architecture](#approval-gate-architecture)
- [Security Validation](#security-validation)
- [Approval Workflow](#approval-workflow)
- [Team Roles and Responsibilities](#team-roles-and-responsibilities)
- [Communication Guidelines](#communication-guidelines)
- [Metrics and Reporting](#metrics-and-reporting)
- [Troubleshooting](#troubleshooting)

## Quick Start

### For Approvers

When you receive an approval notification:

1. **Check your email or GitHub notification**
2. **Click "Review pending deployments"** link
3. **Review the deployment details:**
   - Security scan results
   - Changed files
   - Test results
   - Deployment checklist
4. **Make decision:**
   - ✅ **Approve**: Deployment proceeds
   - ❌ **Reject**: Deployment stops, team notified
5. **Add comment** explaining your decision

### For Developers

To trigger a deployment that requires approval:

**UAT Deployment:**
```bash
# Create and push release branch
git checkout -b release/v1.2.3
git push origin release/v1.2.3
```

**PROD Deployment:**
```bash
# Manual workflow dispatch from main branch
# Go to: Actions → Deploy → Run workflow
```

## Approval Gate Architecture

### Implementation Overview

The gated release strategy is implemented using GitHub Environments with protection rules:

```
┌─────────────────────────────────────────────────────────┐
│                    Deployment Trigger                    │
│              (Push to release/* or manual)               │
└───────────────────┬─────────────────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────────────────┐
│              Pre-Approval Security Checks                │
│  • TruffleHog secret detection                          │
│  • PII/PHI scanning                                      │
│  • Artifact validation                                   │
└───────────────────┬─────────────────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────────────────┐
│                   Approval Gate Job                      │
│         Environment: [UAT/PROD]-approval                 │
│  • Manual approval required                             │
│  • Configured reviewers notified                         │
│  • Wait for approval decision                            │
└───────────────────┬─────────────────────────────────────┘
                    │
          ┌─────────┴─────────┐
          ▼                   ▼
    ✅ Approved          ❌ Rejected
          │                   │
          ▼                   ▼
┌──────────────────┐   ┌──────────────┐
│ Deploy to [ENV]  │   │ Stop & Notify│
└──────────────────┘   └──────────────┘
          │
          ▼
┌──────────────────────────────────────┐
│      Post-Deployment Health Check     │
└──────────────────────────────────────┘
```

### Environment Configuration

#### UAT-approval Environment

**Purpose**: Gate for UAT deployments from release branches

**Required Reviewers:**
- QA Team Lead
- Application Owner
- Senior Developer (optional)

**Protection Rules:**
- Required reviewers: 1-2
- Deployment branches: `release/*` pattern
- Wait timer: 0 minutes (immediate)

**Environment Variables:** None (approval gate only)

#### UAT Environment

**Purpose**: Actual UAT deployment target

**Protection Rules:**
- Deployment branches: `release/*` pattern

**Environment Secrets:**
- `AZURE_CLIENT_ID_UAT`
- `AZURE_TENANT_ID_UAT`
- `AZURE_SUBSCRIPTION_ID_UAT`

#### PROD-approval Environment

**Purpose**: Gate for production deployments

**Required Reviewers:**
- DevOps Manager
- Application Owner
- Compliance Officer
- Operations Manager (optional)

**Protection Rules:**
- Required reviewers: 2-3 (all must approve)
- Deployment branches: Protected branches only (main)
- Wait timer: 0 minutes

**Environment Variables:** None (approval gate only)

#### PROD Environment

**Purpose**: Production deployment target

**Protection Rules:**
- Deployment branches: Protected branches only (main)

**Environment Secrets:**
- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `SFTP_HOST`
- `SFTP_USERNAME`
- `SFTP_PASSWORD`

**Environment Variables:**
- `AZURE_RG_NAME`
- `AZURE_LOCATION`
- `BASE_NAME`
- `IA_NAME`
- `SERVICE_BUS_NAME`
- `STORAGE_SKU`
- `AZURE_CONNECTOR_LOCATION`

## Security Validation

### Pre-Approval Security Checks

Before the approval gate, automated security validation runs:

#### 1. Secrets Detection (TruffleHog)

**Purpose:** Detect hardcoded secrets, API keys, tokens

**Technology:** TruffleHog with verified-only mode

**What it scans:**
- Git commit history
- All files in the repository
- Configuration files
- Infrastructure code

**Blocking criteria:**
- Any verified secret detected
- High-confidence matches

**How to fix:**
```bash
# Remove secret from history
git filter-branch --force --index-filter \
  "git rm --cached --ignore-unmatch path/to/file" \
  --prune-empty --tag-name-filter cat -- --all

# Use environment variables instead
export SECRET_VALUE="xxx"

# Store in GitHub Secrets
gh secret set SECRET_NAME -b "secret-value"
```

#### 2. PII/PHI Detection

**Purpose:** Prevent PHI exposure in code

**Technology:** Custom PowerShell script

**What it scans:**
- Source code files
- Configuration files
- Test data
- Documentation

**Patterns detected:**
- Social Security Numbers (SSN)
- Medical Record Numbers (MRN)
- Patient names in test data
- Phone numbers
- Email addresses in sensitive contexts

**Blocking criteria:**
- Any PII/PHI pattern match
- Test data containing real patient information

**How to fix:**
```powershell
# Use synthetic test data
$testPatient = @{
    SSN = "000-00-0000"
    MRN = "TEST-12345"
    Name = "Test Patient"
}

# Use data masking
$maskedSSN = "XXX-XX-1234"
```

#### 3. Artifact Validation

**Purpose:** Ensure deployment completeness

**What it checks:**
- `infra/main.bicep` exists
- `logicapps/workflows` directory exists
- Workflow JSON files are valid
- Required documentation present

**Blocking criteria:**
- Missing required files
- Invalid file structure

### Security Scan Results in Approval

Reviewers see security scan results in:
1. **Pre-approval-checks job output**
2. **GitHub Actions summary**
3. **Approval notification context**

Example summary:
```
### 🔒 Pre-Approval Security Checks Passed

**Security Validations:**
- ✅ No secrets or credentials detected
- ✅ No PII/PHI in code
- ✅ Deployment artifacts validated

**Deployment Details:**
- Branch: `release/v1.2.3`
- Commit: `abc123...`
- Triggered by: @developer

Ready for approval review. ✅
```

## Approval Workflow

### UAT Approval Process

#### Triggering UAT Deployment

1. **Create release branch:**
   ```bash
   git checkout -b release/v1.2.3
   ```

2. **Make final changes if needed:**
   ```bash
   git add .
   git commit -m "Prepare v1.2.3 release"
   ```

3. **Push to trigger UAT workflow:**
   ```bash
   git push origin release/v1.2.3
   ```

#### Automatic Steps

1. **Pre-approval checks run** (2-3 minutes)
2. **Approval gate activates**
3. **Reviewers notified via:**
   - GitHub notification
   - Email notification
   - Slack/Teams (if configured)

#### Reviewer Actions

**Accessing the approval:**
- Click notification link
- Or: Actions → Running workflow → Review pending deployments

**Review checklist:**
```
UAT Deployment Approval Checklist:

Pre-Deployment Validation:
[ ] All PR checks passed
[ ] Code review completed with approval
[ ] Security scans show no issues
[ ] No secrets or PII/PHI detected
[ ] Unit tests passed

Testing Validation:
[ ] DEV environment tested successfully
[ ] Manual testing completed
[ ] Regression testing passed (if applicable)
[ ] No high-severity bugs open

Documentation:
[ ] Release notes updated
[ ] CHANGELOG.md updated
[ ] API changes documented (if applicable)

Compliance:
[ ] No high-severity security issues
[ ] HIPAA compliance requirements met
[ ] Test data is synthetic (no real PHI)
```

**Making the decision:**
- ✅ **Approve**: Click "Approve and deploy"
- ❌ **Reject**: Click "Reject", add comment with reason
- 💬 **Comment**: Add notes for audit trail

#### Post-Approval

1. **Validation job continues**
2. **Infrastructure deployment** (5-10 min)
3. **Logic Apps deployment** (2-3 min)
4. **Health checks run automatically**
5. **Team notified of completion**

### PROD Approval Process

#### Triggering PROD Deployment

**Prerequisites:**
- [ ] UAT deployment successful
- [ ] UAT testing complete and signed off
- [ ] Change management ticket approved
- [ ] Rollback plan documented
- [ ] Stakeholders notified
- [ ] Deployment window scheduled

**Triggering:**
1. Go to **Actions** → **Deploy** workflow
2. Click **Run workflow**
3. Select **main** branch
4. Confirm and run

#### Automatic Steps

1. **Pre-approval security checks** (2-3 minutes)
2. **Approval gate activates**
3. **ALL configured reviewers notified**

#### Reviewer Actions

**Production approval requires stricter validation:**

```
PROD Deployment Approval Checklist:

UAT Validation:
[ ] UAT deployment completed successfully
[ ] UAT testing completed and documented
[ ] All UAT issues resolved or documented
[ ] UAT sign-off obtained

Security & Compliance:
[ ] Security scans passed (no critical issues)
[ ] No secrets or credentials in code
[ ] Compliance review completed
[ ] HIPAA requirements verified
[ ] ARM What-If analysis reviewed

Change Management:
[ ] Change ticket approved: CHG______
[ ] Deployment window scheduled
[ ] Rollback plan documented and tested
[ ] Stakeholders notified

Operations:
[ ] Backup verified and tested
[ ] Monitoring alerts configured
[ ] On-call team notified
[ ] Health check criteria defined
[ ] No concurrent changes scheduled

Documentation:
[ ] Release notes finalized
[ ] Runbooks updated
[ ] Known issues documented
```

**Decision requirements:**
- **Minimum approvers:** 2-3 (all must approve)
- **Approval must include:** Comment with review notes
- **Rejection:** Requires detailed explanation

#### Post-Approval

1. **Infrastructure deployment** (5-10 min)
2. **ARM What-If analysis logged**
3. **Logic Apps deployment** (2-3 min)
4. **Automated health checks**
5. **Post-deployment monitoring begins**
6. **Team notified of completion**

## Team Roles and Responsibilities

### Developers

**Responsibilities:**
- Create release branches for UAT
- Ensure all tests pass before triggering
- Respond to approval rejections
- Fix issues identified during review
- Update documentation
- Participate in post-deployment verification

**Best Practices:**
- Test thoroughly in DEV before UAT
- Include change ticket in commit messages
- Update CHANGELOG.md
- Document breaking changes
- Provide clear release notes

### QA Team

**Responsibilities:**
- Review and approve UAT deployments
- Execute UAT test plans
- Sign off on UAT success
- Report issues found in UAT
- Verify fixes before PROD approval

**UAT Approval Criteria:**
- DEV testing completed
- Test plan available
- No blocking issues
- Regression testing passed

### DevOps Team

**Responsibilities:**
- Review infrastructure changes
- Approve both UAT and PROD deployments
- Monitor deployment health
- Execute rollbacks if needed
- Maintain deployment documentation
- Configure approval gates

**Approval Focus:**
- Infrastructure changes
- Security implications
- Performance impact
- Operational considerations

### Application Owner

**Responsibilities:**
- Approve UAT and PROD deployments
- Sign off on UAT testing
- Make final PROD deployment decision
- Coordinate with stakeholders
- Manage deployment schedule

**Decision Criteria:**
- Business impact acceptable
- Timing appropriate
- Stakeholders informed
- Risk acceptable

### Compliance Officer

**Responsibilities:**
- Approve PROD deployments
- Verify HIPAA compliance
- Review security scans
- Audit approval logs
- Ensure documentation complete

**Approval Criteria:**
- No PHI exposure risk
- Security requirements met
- Audit logging configured
- Compliance documentation updated

## Communication Guidelines

### Deployment Notifications

#### Starting a UAT Deployment

**Slack/Teams Message:**
```
🚀 UAT Deployment Started - Release v1.2.3

Status: Awaiting Approval
Approvers: @qa-team @app-owner

Details:
• Branch: release/v1.2.3
• Changes: HIPAA 275 processing enhancements
• Security: ✅ All checks passed

Review: [GitHub Actions Link]
```

#### UAT Approval Granted

```
✅ UAT Deployment Approved

Approved by: @qa-lead
Status: Deploying to UAT

ETA: 10-15 minutes
Monitoring: [App Insights Link]
```

#### UAT Deployment Complete

```
✅ UAT Deployment Complete - Release v1.2.3

Duration: 12 minutes
Status: Healthy
Health Checks: All Passed

Next Steps:
• Run UAT test suite
• Verify workflows active
• Check Application Insights

UAT Environment: [Portal Link]
```

#### Starting a PROD Deployment

**Email Template:**
```
Subject: PROD Deployment - Approval Required - Release v1.2.3

Team,

A production deployment is ready and requires your approval.

DEPLOYMENT DETAILS:
Release: v1.2.3
Branch: main
Triggered by: Release Manager
Window: Today 10:00-11:00 AM EST

CHANGES:
• HIPAA 275 processing enhancements
• Service Bus retry logic improvements
• Updated X12 schema validation

UAT VALIDATION:
• Deployed: 2024-11-15 14:30
• Testing: Complete
• Issues: None
• Sign-off: QA Team Lead

SECURITY:
✅ All security scans passed
✅ No secrets detected
✅ No PII/PHI in code

APPROVALS REQUIRED:
@devops-manager @app-owner @compliance-officer

REVIEW & APPROVE:
[GitHub Actions Approval Link]

ROLLBACK PLAN:
Previous version: v1.2.2
Rollback time: < 5 minutes
Procedure: See DEPLOYMENT.md

Questions? Contact: Release Manager
```

#### PROD Approval Granted

```
✅ PROD Deployment Approved

Approved by:
• @devops-manager
• @app-owner  
• @compliance-officer

Status: Deploying to Production
Window: 10:00-11:00 AM EST

All stakeholders notified.
Monitoring enabled.

Live Status: [GitHub Actions Link]
```

#### PROD Deployment Complete

```
🎉 PROD Deployment Complete - Release v1.2.3

Duration: 14 minutes
Status: Healthy
Health Checks: All Passed ✅

Deployed Resources:
• Logic App: ✅ Running
• Workflows: ✅ 6/6 Enabled
• Service Bus: ✅ Connected
• Storage: ✅ Accessible

Monitoring:
• Application Insights: [Link]
• Azure Portal: [Link]
• Health Dashboard: [Link]

Post-Deployment:
• Monitoring alerts active
• On-call team notified
• Health checks scheduled

Thank you for your support!
```

#### Deployment Rejected

```
⚠️ Deployment Rejected - Action Required

Environment: [UAT/PROD]
Release: v1.2.3
Rejected by: @reviewer

Reason:
[Reviewer's comment explaining rejection]

Required Actions:
1. Address reviewer concerns
2. Fix identified issues
3. Re-test in DEV
4. Re-trigger deployment

Need Help?
Contact: @devops-team

Incident: [Link to issue]
```

### Emergency Communications

#### Emergency Hotfix Required

```
🚨 EMERGENCY DEPLOYMENT - Approval Required

Severity: Critical
Issue: [Brief description]
Impact: [Production impact]

EMERGENCY CRITERIA MET:
☑️ Production system degraded/down
☑️ Data integrity issue
☑️ Security vulnerability
☑️ HIPAA compliance violation

REQUIRED APPROVERS:
@on-call-lead @app-owner @compliance-officer

APPROVAL WINDOW: 30 minutes

Details: [Incident link]
Fix: [PR link]
Testing: [Test results]

EXPEDITED REVIEW REQUIRED
[Approval Link]
```

## Metrics and Reporting

### Key Performance Indicators

#### Deployment Metrics

**Tracked Metrics:**
- **Deployment Frequency**: Deployments per week by environment
- **Approval Time**: Time from trigger to approval
- **Deployment Duration**: Time from approval to completion
- **Success Rate**: Percentage of successful deployments
- **Rollback Rate**: Percentage requiring rollback
- **Approval Rejection Rate**: Percentage of rejected deployments

**Targets:**
- UAT deployment frequency: ≥ 2 per week
- PROD deployment frequency: ≥ 1 per week
- Average approval time (UAT): < 2 hours
- Average approval time (PROD): < 4 hours
- Deployment success rate: ≥ 95%
- Rollback rate: < 5%

#### Approval Metrics

**Tracked Metrics:**
- Number of approvals per reviewer
- Average time to approve
- Rejection reasons (categorized)
- After-hours approvals
- Emergency approvals

**Reporting:**
- Weekly deployment summary
- Monthly approval metrics
- Quarterly trends analysis
- Annual compliance report

### Generating Reports

#### Monthly Deployment Report

```bash
#!/bin/bash
# generate-deployment-report.sh

MONTH=$(date -d "last month" +%Y-%m)
REPORT_FILE="deployment-report-$MONTH.md"

echo "# Deployment Report - $MONTH" > $REPORT_FILE
echo "" >> $REPORT_FILE

# Get deployment data from GitHub
gh api /repos/{owner}/{repo}/actions/runs \
  --jq ".workflow_runs[] | select(.created_at | startswith(\"$MONTH\")) | {name: .name, status: .status, conclusion: .conclusion}" | \
  jq -s 'group_by(.name) | map({workflow: .[0].name, total: length, success: map(select(.conclusion == "success")) | length})' \
  >> $REPORT_FILE

# Get approval data
echo "## Approval Metrics" >> $REPORT_FILE
gh api /repos/{owner}/{repo}/actions/runs \
  --jq ".workflow_runs[] | select(.created_at | startswith(\"$MONTH\")) | {created: .created_at, updated: .updated_at, duration: (.updated_at | fromdateiso8601) - (.created_at | fromdateiso8601)}" | \
  jq -s 'add / length | "Average approval time: \(. / 60) minutes"' \
  >> $REPORT_FILE

echo "Report generated: $REPORT_FILE"
```

### Dashboard Queries

**Application Insights - Deployment Correlation:**

```kusto
customEvents
| where timestamp > ago(30d)
| where name == "deployment_completed"
| extend 
    environment = tostring(customDimensions["environment"]),
    status = tostring(customDimensions["status"]),
    duration = todouble(customDimensions["duration_minutes"])
| summarize 
    TotalDeployments = count(),
    SuccessfulDeployments = countif(status == "success"),
    AverageDuration = avg(duration)
    by environment
| extend SuccessRate = (SuccessfulDeployments * 100.0) / TotalDeployments
| project environment, TotalDeployments, SuccessRate, AverageDuration
```

**Azure Monitor - Deployment Activities:**

```bash
# Last 30 days deployment activities
az monitor activity-log list \
  --resource-group "payer-attachments-prod-rg" \
  --start-time $(date -u -d '30 days ago' '+%Y-%m-%dT%H:%M:%SZ') \
  --query "[?contains(operationName.value, 'deployments')].{Time:eventTimestamp, Caller:caller, Operation:operationName.localizedValue, Status:status.localizedValue}" \
  --output table
```

## Troubleshooting

### Common Issues

#### Approval Not Receiving Notifications

**Symptoms:**
- Reviewers not getting email
- No GitHub notification received

**Solutions:**
1. **Check GitHub notification settings:**
   - Settings → Notifications
   - Ensure "Actions" notifications enabled

2. **Verify email settings:**
   - Settings → Emails
   - Confirm email address verified

3. **Check environment configuration:**
   - Repository → Settings → Environments
   - Verify reviewers configured correctly

4. **Test notifications:**
   ```bash
   # Trigger test deployment
   gh workflow run deploy-uat.yml
   ```

#### Cannot Approve Deployment

**Symptoms:**
- "Review pending deployments" button not showing
- Access denied when trying to approve

**Solutions:**
1. **Verify you're a configured reviewer:**
   - Repository → Settings → Environments → [ENV]-approval
   - Check your username in Required reviewers list

2. **Check environment access:**
   - You must have appropriate repository permissions
   - Minimum: Write access to repository

3. **Clear browser cache:**
   - Logout and login to GitHub
   - Try different browser

4. **Contact repository admin:**
   - Request reviewer access
   - Verify team membership

#### Security Scan Failures

**Symptoms:**
- Pre-approval checks failing
- TruffleHog or PII/PHI scan detecting issues

**Solutions:**

**For secret detection:**
```bash
# Remove secret from history
git filter-branch --force --index-filter \
  "git rm --cached --ignore-unmatch path/to/file" \
  --prune-empty --tag-name-filter cat -- --all

# Use secrets instead
gh secret set SECRET_NAME

# Force push (careful!)
git push origin --force --all
```

**For PII/PHI detection:**
```bash
# Replace real data with synthetic
sed -i 's/123-45-6789/000-00-0000/g' test-data.json

# Commit and re-trigger
git add test-data.json
git commit -m "Replace real data with synthetic test data"
git push
```

#### Deployment Stuck at Approval

**Symptoms:**
- Workflow running for hours
- No approval or rejection

**Solutions:**
1. **Check if approval needed:**
   - Look for "Review pending deployments" button
   - Verify correct environment

2. **Ping reviewers:**
   - Send Slack/Teams reminder
   - Check if reviewers available

3. **Cancel and re-trigger:**
   ```bash
   # Cancel current run
   gh run cancel <run-id>
   
   # Re-trigger
   gh workflow run deploy-uat.yml
   ```

4. **For emergencies:**
   - Contact on-call manager
   - Follow emergency approval procedure

### Getting Help

**Support Channels:**
- **Slack:** #deployments channel
- **Teams:** DevOps Team
- **Email:** devops@company.com
- **On-Call:** Check PagerDuty rotation

**Documentation:**
- [DEPLOYMENT.md](DEPLOYMENT.md) - Full deployment guide
- [SECURITY.md](SECURITY.md) - Security procedures
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Detailed troubleshooting

**Escalation Path:**
1. DevOps Team Member
2. DevOps Lead
3. Engineering Manager
4. On-Call Engineering Lead

---

## Appendix

### Configuration Examples

#### GitHub Environment Setup (via UI)

1. **Go to Repository Settings**
2. **Click Environments**
3. **Click "New environment"**
4. **Enter name:** `PROD-approval`
5. **Configure protection rules:**
   - ☑️ Required reviewers
   - Add: devops-manager, app-owner, compliance-officer
   - Require all reviewers: Yes
6. **Configure deployment branches:**
   - ☑️ Protected branches only
7. **Save protection rules**

#### GitHub Environment Setup (via CLI)

```bash
# Create environment (requires admin access)
gh api -X PUT /repos/{owner}/{repo}/environments/PROD-approval \
  --input - <<EOF
{
  "reviewers": [
    {
      "type": "User",
      "id": 12345
    }
  ],
  "deployment_branch_policy": {
    "protected_branches": true,
    "custom_branch_policies": false
  }
}
EOF
```

### Useful Commands

```bash
# List all workflow runs
gh run list --workflow=deploy.yml --limit 20

# View specific run
gh run view <run-id> --log

# Cancel stuck run
gh run cancel <run-id>

# Re-run failed run
gh run rerun <run-id>

# List pending approvals
gh run list --status waiting

# Get approval URL
gh run view <run-id> --web
```

### Compliance Checklist

```
Monthly Compliance Review:

Audit Logs:
[ ] GitHub deployment logs reviewed
[ ] Azure activity logs reviewed
[ ] Application Insights logs checked
[ ] No unauthorized access detected

Approvals:
[ ] All deployments properly approved
[ ] Approval times within SLA
[ ] Rejection reasons documented
[ ] Emergency approvals justified

Security:
[ ] All security scans passing
[ ] No secrets in code
[ ] No PII/PHI exposure
[ ] Vulnerabilities addressed

Documentation:
[ ] Deployment logs archived
[ ] Change tickets linked
[ ] Rollback procedures tested
[ ] Runbooks updated

Training:
[ ] New reviewers trained
[ ] Procedures reviewed with team
[ ] Emergency contacts updated
```

---

**Document Version:** 1.0  
**Last Updated:** 2024-11-17  
**Owner:** DevOps Team  
**Review Frequency:** Quarterly
