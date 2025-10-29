# PowerShell Scripts Instructions

**Applies to**: `*.ps1` files in repository root and `scripts/` directory

## Overview

PowerShell scripts for repository management, deployment automation, and workflow testing.

## Script Files

### Repository Management
- **bootstrap_repo.ps1**: Creates new repository from template
- **fix_repo_structure.ps1**: Normalizes repo structure to Logic Apps deployment layout

### Deployment Scripts
- **deploy-workflows.ps1**: Deploys Logic App workflows
- **deploy-new-integration-account.ps1**: Creates and configures Integration Account
- **deploy-api-connections.json.ps1**: Deploys API connection configurations

### Configuration Scripts  
- **setup-integration-account.ps1**: Configures X12 schemas and trading partners
- **configure-x12-agreements.ps1**: Sets up X12 trading partner agreements
- **configure-hipaa-trading-partners.ps1**: Configures HIPAA-specific partner settings

### Testing Scripts
- **test-workflows.ps1**: Comprehensive workflow testing framework

## Best Practices

### PowerShell Conventions
- Use approved PowerShell verbs (Get, Set, New, Remove, etc.)
- Follow PascalCase for function and parameter names
- Use kebab-case for file names (e.g., `fix-repo-structure.ps1`)
- Include comment-based help for all functions
- Use `[CmdletBinding()]` for advanced functions

### Parameter Definitions
```powershell
param(
    [Parameter(Mandatory=$true, HelpMessage="Path to repository root")]
    [ValidateNotNullOrEmpty()]
    [string]$RepoRoot,
    
    [Parameter(Mandatory=$false)]
    [switch]$Force
)
```

### Error Handling
```powershell
try {
    # Main logic
}
catch {
    Write-Error "Operation failed: $_"
    exit 1
}
finally {
    # Cleanup
}
```

### Script Structure
```powershell
<#
.SYNOPSIS
    Brief description
.DESCRIPTION
    Detailed description
.PARAMETER ParamName
    Parameter description
.EXAMPLE
    Example usage
#>
[CmdletBinding()]
param(...)

# Strict mode for better error detection
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Main script logic
```

## Key Scripts

### fix_repo_structure.ps1
**Purpose**: Ensures repository structure matches Logic Apps deployment requirements  
**Usage**: `pwsh -c "./fix_repo_structure.ps1 -RepoRoot ."`  
**Timing**: <1 second (set timeout: 30+ seconds)

**When to Run**:
- After cloning repository
- Before creating deployment package
- If workflows are in unexpected locations
- As pre-commit validation

### test-workflows.ps1
**Purpose**: Comprehensive testing framework for Logic App workflows  
**Usage Examples**:
```powershell
# Test inbound 275 processing
pwsh -c "./test-workflows.ps1 -TestInbound275"

# Test outbound 277 RFAI
pwsh -c "./test-workflows.ps1 -TestOutbound277"

# Full end-to-end workflow
pwsh -c "./test-workflows.ps1 -TestFullWorkflow"

# Custom environment
pwsh -c "./test-workflows.ps1 -TestInbound275 -ResourceGroup 'my-rg' -LogicAppName 'my-la'"
```

**Trading Partners**:
- Sender: Availity (030240928)
- Receiver: PCHP-QNXT (66917)

### bootstrap_repo.ps1
**Purpose**: Creates new repository from template  
**Usage**: For creating new HIPAA Attachments deployments  
**Note**: Typically used once during initial setup

### deploy-workflows.ps1
**Purpose**: Deploys Logic App workflows to Azure  
**Prerequisites**:
- Azure CLI authenticated
- Valid resource group and Logic App
- workflows.zip package created

## Validation

### Syntax Validation
```bash
# Check PowerShell syntax
pwsh -Command "Get-Content './test-workflows.ps1' | Out-Null"

# Validate all .ps1 files
find . -name "*.ps1" -print0 | while IFS= read -r -d '' f; do
  echo "Checking $f"
  pwsh -Command "Get-Content '$f' | Out-Null"
done
```

### Automated Checks
The `pr-lint.yml` workflow validates:
- PowerShell syntax errors
- Script can be loaded without errors

### Local Testing
```powershell
# Import script as module to test functions
. ./test-workflows.ps1

# Run with -WhatIf to preview changes
./deploy-workflows.ps1 -WhatIf

# Use -Verbose for detailed output
./fix_repo_structure.ps1 -RepoRoot . -Verbose
```

## Common Scenarios

### Adding a New Script
1. Create script with proper header comments
2. Use `[CmdletBinding()]` for parameters
3. Implement error handling with try/catch
4. Add examples in comment-based help
5. Test syntax validation locally
6. Update this instructions file if it's significant

### Modifying Existing Scripts
1. Understand current functionality first
2. Preserve existing parameter names (backward compatibility)
3. Add new parameters as optional with defaults
4. Test in non-production environment
5. Update comment-based help

### Debugging Scripts
```powershell
# Enable detailed tracing
Set-PSDebug -Trace 2

# Use breakpoints in VS Code
# Add: Wait-Debugger

# Write debug messages
Write-Debug "Variable value: $myVar" -Debug

# Verbose output
Write-Verbose "Processing item: $item" -Verbose
```

## Azure CLI Integration

### Authentication
```powershell
# Check if logged in
az account show

# Login if needed
az login

# Set subscription
az account set --subscription "subscription-id"
```

### Resource Operations
```powershell
# Get Logic App details
$logicApp = az webapp show `
  --resource-group $ResourceGroup `
  --name $LogicAppName | ConvertFrom-Json

# Deploy workflows
az webapp deploy `
  --resource-group $ResourceGroup `
  --name $LogicAppName `
  --src-path workflows.zip `
  --type zip

# Restart Logic App
az webapp restart `
  --resource-group $ResourceGroup `
  --name $LogicAppName
```

## Testing Workflows

### Test File References
- `test-x12-275-availity-to-pchp.edi`: Sample 275 EDI file
- `test-qnxt-response-payload.json`: Sample QNXT API response
- `test-plan-trading-partners.md`: Detailed test plan
- `testing-status-report.md`: Testing status and results

### Running Tests
```powershell
# Quick validation test
./test-workflows.ps1 -TestInbound275 -ResourceGroup "dev-rg" -LogicAppName "dev-la"

# Full workflow with verbose output
./test-workflows.ps1 -TestFullWorkflow -Verbose

# Dry run (if script supports -WhatIf)
./test-workflows.ps1 -TestInbound275 -WhatIf
```

## Security Considerations

### Secrets Management
- **Never** hardcode credentials in scripts
- Use Azure Key Vault for sensitive values
- Accept secrets as secure string parameters
- Clear sensitive variables after use

```powershell
# Secure string parameter
param(
    [Parameter(Mandatory=$true)]
    [SecureString]$ApiKey
)

# Convert for use
$apiKeyPlainText = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($ApiKey))

# Clear when done
Clear-Variable apiKeyPlainText
```

### Managed Identity
- Prefer managed identity over service principals
- Use `az account get-access-token` for token-based auth
- Avoid storing credentials in environment variables

## Common Pitfalls

### Execution Policy
- May need to set execution policy for unsigned scripts
- Use `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass` temporarily
- Consider signing scripts for production use

### Path Handling
- Always use `Join-Path` for cross-platform compatibility
- Avoid hardcoded backslashes or forward slashes
- Use `[System.IO.Path]::Combine()` for complex paths

### Variable Scoping
- Be careful with global variables
- Use `$script:` scope for script-level variables
- Clear sensitive variables when done

### Error Propagation
- Set `$ErrorActionPreference = "Stop"` for consistent error handling
- Use try/catch for expected errors
- Always provide meaningful error messages

## Performance Tips

### Parallel Execution
```powershell
# Use ForEach-Object -Parallel (PS 7+)
$items | ForEach-Object -Parallel {
    # Parallel operation
} -ThrottleLimit 5
```

### Efficient Azure CLI Calls
```powershell
# Cache results instead of repeated calls
$logicApp = az webapp show --resource-group $rg --name $name | ConvertFrom-Json

# Use --output table for human-readable output
az webapp list --output table

# Use --query for filtering (reduce data transfer)
az webapp list --query "[?state=='Running'].{name:name}" -o table
```

## Documentation

### Comment-Based Help
Always include:
- `.SYNOPSIS`: One-line description
- `.DESCRIPTION`: Detailed explanation
- `.PARAMETER`: For each parameter
- `.EXAMPLE`: At least one usage example
- `.NOTES`: Any important notes

### Inline Comments
- Explain complex logic
- Document non-obvious decisions
- Mark TODO items for future work
- Reference related documentation or issues
