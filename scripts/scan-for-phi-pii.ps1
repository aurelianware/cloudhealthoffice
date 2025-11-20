<#
.SYNOPSIS
    Scans code files for potential PII/PHI data and HIPAA compliance issues.

.DESCRIPTION
    This script scans source code, configuration, and documentation files for:
    - Social Security Numbers (SSN)
    - Medical Record Numbers (MRN)
    - Patient names in test data
    - Credit card numbers
    - Date of birth patterns
    - Protected Health Information (PHI)
    - Hardcoded credentials
    - API keys and secrets
    - HIPAA compliance violations

.PARAMETER Path
    Path to scan. Can be a file or directory.

.PARAMETER Exclude
    Array of paths or patterns to exclude from scanning.

.PARAMETER FailOnWarning
    Treat warnings as failures (exit with non-zero code).

.EXAMPLE
    .\scan-for-phi-pii.ps1 -Path "."
    Scans current directory for PII/PHI issues.

.EXAMPLE
    .\scan-for-phi-pii.ps1 -Path "." -Exclude ".git","node_modules" -FailOnWarning
    Scans with exclusions and treats warnings as failures.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true, HelpMessage="Path to scan")]
    [ValidateNotNullOrEmpty()]
    [string]$Path,
    
    [Parameter(Mandatory=$false, HelpMessage="Paths to exclude")]
    [string[]]$Exclude = @('.git', 'node_modules', '.terraform', 'dist', 'build', 'bin', 'obj'),
    
    [Parameter(Mandatory=$false, HelpMessage="Fail on warnings")]
    [switch]$FailOnWarning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Scanning patterns
$PHI_PII_PATTERNS = @{
    'SSN' = @{
        Pattern = '\b\d{3}-\d{2}-\d{4}\b'
        Description = 'Social Security Number (formatted)'
        Severity = 'Critical'
        AllowedFiles = @('*.edi', 'test-*.json', 'test-*.edi', '*.md')
        ExcludeContext = @('030240928', '{config.payerId}', 'Availity', 'Health Plan', 'trading', 'partner', 'ISA', 'GS')
    }
    'MRN' = @{
        Pattern = '\bMRN[:\s]*[A-Z0-9]{6,12}\b'
        Description = 'Medical Record Number'
        Severity = 'High'
        AllowedFiles = @('*.edi', 'test-*.json', 'test-*.edi')
    }
    'DOB' = @{
        Pattern = '\b(?:DOB|DateOfBirth|Birth[_\s]?Date)[:\s]*\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b'
        Description = 'Date of Birth'
        Severity = 'High'
        AllowedFiles = @('*.edi', 'test-*.json', 'test-*.edi')
    }
    'CreditCard' = @{
        Pattern = '\b(?:4\d{3}|5[1-5]\d{2}|6011|3[47]\d{2})\s?\d{4}\s?\d{4}\s?\d{4}\b'
        Description = 'Credit Card Number'
        Severity = 'Critical'
        AllowedFiles = @()
    }
    'Email' = @{
        Pattern = '\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b'
        Description = 'Email Address'
        Severity = 'Medium'
        AllowedFiles = @('*.md', '*.ps1', 'test-*.json', '*-config.json', 'example-*.json', '*.yml', '*.yaml', '*.test.ts', '*.test.js', '*.spec.ts', '*.spec.js', '*test*.ts', '*test*.js')
        ExcludeContext = @('test\.com|example\.com|sample\.com|mock\.|dummy\.|placeholder|@test\.|john@|jane@|user@test')
    }
    'PhoneNumber' = @{
        Pattern = '\b(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b'
        Description = 'Phone Number'
        Severity = 'Medium'
        AllowedFiles = @('*.edi', 'test-*.json', '*-config.json', 'example-*.json', 'ecs-*.json', '*valueadds*.json', '*.md', '*.yml', '*.yaml', '*.ts', '*.js')
        ExcludeContext = @('npi|provider|tax|ein|test|example|sample|mock|dummy|placeholder|1234567890|9999', 'TEST-', 'providerId', 'providerNpi')
    }
    'MemberId' = @{
        Pattern = '\b(?:Member[_\s]?ID|MemberId)[:\s]*[A-Z0-9]{6,15}\b'
        Description = 'Member/Patient ID'
        Severity = 'High'
        AllowedFiles = @('*.edi', 'test-*.json', 'test-*.edi', '*-config.json', 'example-*.json', '*.md', 'test-*.ps1', '*.yml', '*.yaml')
    }
}

$SECRET_PATTERNS = @{
    'AzureKey' = @{
        Pattern = 'azure[_-]?(?:key|secret|token)["\s:=]+[a-zA-Z0-9+/=]{20,}'
        Description = 'Azure API Key/Secret'
        Severity = 'Critical'
        AllowedFiles = @()
    }
    'GenericPassword' = @{
        Pattern = '(?:password|pwd|passwd)["'']\s*[:=]\s*["''][^"''<>\s]{8,}["'']'
        Description = 'Hardcoded Password'
        Severity = 'Critical'
        AllowedFiles = @('*.md', 'test-*.ps1', 'deploy-*.ps1', '*.bicep')
        ExcludeContext = @('sftpPassword', 'sftpUsername', '<your-password>', 'example', 'placeholder', '\$\{', 'param\(', '@secure')
    }
    'ConnectionString' = @{
        Pattern = '(?:DefaultEndpointsProtocol=|AccountKey=|SharedAccessSignature=)[a-zA-Z0-9+/=]{20,}'
        Description = 'Azure Connection String'
        Severity = 'Critical'
        AllowedFiles = @()
    }
    'PrivateKey' = @{
        Pattern = '-----BEGIN (?:RSA |EC |DSA )?PRIVATE KEY-----'
        Description = 'Private Key'
        Severity = 'Critical'
        AllowedFiles = @()
    }
    'JWTToken' = @{
        Pattern = 'eyJ[a-zA-Z0-9_-]*\.eyJ[a-zA-Z0-9_-]*\.[a-zA-Z0-9_-]*'
        Description = 'JWT Token'
        Severity = 'High'
        AllowedFiles = @('*.md')
    }
}

$HIPAA_PATTERNS = @{
    'UnencryptedPHI' = @{
        Pattern = '(?:http://|ftp://).*(?:patient|phi|medical|health).*'
        Description = 'Unencrypted PHI transmission (should use HTTPS)'
        Severity = 'Critical'
        AllowedFiles = @('*.md', 'scan-for-phi-pii.ps1')
    }
    'LoggingPHI' = @{
        Pattern = '(?:console\.log|Write-Host|console\.warn|console\.error|logger\.|log\.).*(?:ssn|social.?security|dob|date.?of.?birth|patient|member.?id|mrn|medical.?record)'
        Description = 'Potential PHI in logs'
        Severity = 'High'
        AllowedFiles = @('scan-for-phi-pii.ps1', '*.md', '*test*.ts', '*test*.js', '*.test.ts', '*.test.js', '*.spec.ts', '*.spec.js')
        ExcludeContext = @('example', 'test', 'mock', 'dummy', 'sample', 'synthetic', 'placeholder', 'TODO', 'FIXME')
    }
}

# Scan results
$script:Findings = @()
$script:FilesScanned = 0
$script:IssuesFound = 0

# Pre-compile all regex patterns at script scope for better performance
$script:CompiledPatterns = @{}
foreach ($patternName in $PHI_PII_PATTERNS.Keys) {
    $script:CompiledPatterns[$patternName] = @{
        Regex = [regex]::new($PHI_PII_PATTERNS[$patternName].Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Compiled)
        Config = $PHI_PII_PATTERNS[$patternName]
        Category = 'PHI_PII'
    }
}
foreach ($patternName in $SECRET_PATTERNS.Keys) {
    $script:CompiledPatterns[$patternName] = @{
        Regex = [regex]::new($SECRET_PATTERNS[$patternName].Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Compiled)
        Config = $SECRET_PATTERNS[$patternName]
        Category = 'SECRET'
    }
}
foreach ($patternName in $HIPAA_PATTERNS.Keys) {
    $script:CompiledPatterns[$patternName] = @{
        Regex = [regex]::new($HIPAA_PATTERNS[$patternName].Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Compiled)
        Config = $HIPAA_PATTERNS[$patternName]
        Category = 'HIPAA'
    }
}

function Test-FileExcluded {
    param([string]$FilePath)
    
    foreach ($pattern in $Exclude) {
        if ($FilePath -like "*$pattern*") {
            return $true
        }
    }
    return $false
}

function Test-FileAllowed {
    param([string]$FilePath, [array]$AllowedPatterns)
    
    if ($AllowedPatterns.Count -eq 0) {
        return $false
    }
    
    $fileName = Split-Path $FilePath -Leaf
    foreach ($pattern in $AllowedPatterns) {
        if ($fileName -like $pattern) {
            return $true
        }
    }
    return $false
}

function Add-Finding {
    param(
        [string]$File,
        [int]$Line,
        [string]$Type,
        [string]$Description,
        [string]$Severity,
        [string]$Match
    )
    
    $script:IssuesFound++
    $script:Findings += @{
        File = $File
        Line = $Line
        Type = $Type
        Description = $Description
        Severity = $Severity
        Match = $Match
    }
    
    # Output in GitHub Actions format
    $level = switch ($Severity) {
        'Critical' { 'error' }
        'High' { 'error' }
        'Medium' { 'warning' }
        'Low' { 'notice' }
        default { 'warning' }
    }
    
    Write-Host "::$level file=$File,line=$Line::[$Type] $Description - Found: $($Match.Substring(0, [Math]::Min(50, $Match.Length)))..."
}

function Scan-FileContent {
    param(
        [string]$FilePath
    )
    
    # Skip binary files
    $extension = [System.IO.Path]::GetExtension($FilePath).ToLower()
    $binaryExtensions = @('.dll', '.exe', '.bin', '.zip', '.tar', '.gz', '.pdf', '.jpg', '.png', '.gif')
    if ($binaryExtensions -contains $extension) {
        return
    }
    
    # Skip large files (> 1MB)
    $fileInfo = Get-Item $FilePath
    if ($fileInfo.Length -gt 1MB) {
        Write-Verbose "Skipping large file: $FilePath"
        return
    }
    
    try {
        $content = Get-Content -Path $FilePath -Raw -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($content)) {
            return
        }
        
        $lines = $content -split "`r?`n"
        $script:FilesScanned++
        
        # Single pass through lines, checking all patterns (using pre-compiled patterns from script scope)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            
            # Skip empty lines
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }
            
            foreach ($patternName in $script:CompiledPatterns.Keys) {
                $patternInfo = $script:CompiledPatterns[$patternName]
                $pattern = $patternInfo.Config
                $regex = $patternInfo.Regex
                
                # Check if file is in allowed list (do this before regex matching)
                if (Test-FileAllowed $FilePath $pattern.AllowedFiles) {
                    continue
                }
                
                $matches = $regex.Matches($line)
                
                if ($matches.Count -gt 0) {
                    foreach ($match in $matches) {
                        # Check for excluded context (for all pattern types)
                        $skipMatch = $false
                        if ($pattern.ContainsKey('ExcludeContext')) {
                            foreach ($excludeWord in $pattern.ExcludeContext) {
                                if ($line -match $excludeWord) {
                                    $skipMatch = $true
                                    break
                                }
                            }
                        }
                        
                        if (-not $skipMatch) {
                            Add-Finding -File $FilePath -Line ($i + 1) -Type $patternName `
                                -Description $pattern.Description -Severity $pattern.Severity `
                                -Match $match.Value
                        }
                    }
                }
            }
        }
        
    } catch {
        Write-Verbose "Error reading file $FilePath : $_"
    }
}

function Scan-Directory {
    param([string]$DirectoryPath)
    
    $files = Get-ChildItem -Path $DirectoryPath -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { -not (Test-FileExcluded $_.FullName) }
    
    Write-Host "🔍 Scanning $($files.Count) files..."
    
    foreach ($file in $files) {
        Scan-FileContent $file.FullName
    }
}

# Main execution
Write-Host "🔐 HIPAA PII/PHI Security Scanner"
Write-Host "================================="

$targetPath = Resolve-Path $Path -ErrorAction SilentlyContinue
if (-not $targetPath) {
    Write-Host "::error::Path not found: $Path"
    exit 1
}

$isDirectory = Test-Path $targetPath -PathType Container

if ($isDirectory) {
    Scan-Directory $targetPath
} else {
    Scan-FileContent $targetPath
}

# Summary
Write-Host "`n" + ("=" * 60)
Write-Host "📊 Security Scan Summary"
Write-Host ("=" * 60)
Write-Host "Files scanned: $script:FilesScanned"
Write-Host "Issues found: $script:IssuesFound"

if ($script:Findings.Count -gt 0) {
    # Group by severity
    $critical = @($script:Findings | Where-Object { $_.Severity -eq 'Critical' })
    $high = @($script:Findings | Where-Object { $_.Severity -eq 'High' })
    $medium = @($script:Findings | Where-Object { $_.Severity -eq 'Medium' })
    $low = @($script:Findings | Where-Object { $_.Severity -eq 'Low' })
    
    if ($critical.Count -gt 0) {
        Write-Host "`n🔴 Critical Issues ($($critical.Count)):"
        $critical | ForEach-Object { 
            Write-Host "  [$($_.File):$($_.Line)] $($_.Description)" 
        }
    }
    
    if ($high.Count -gt 0) {
        Write-Host "`n🟠 High Severity Issues ($($high.Count)):"
        $high | ForEach-Object { 
            Write-Host "  [$($_.File):$($_.Line)] $($_.Description)" 
        }
    }
    
    if ($medium.Count -gt 0) {
        Write-Host "`n🟡 Medium Severity Issues ($($medium.Count)):"
        $medium | ForEach-Object { 
            Write-Host "  [$($_.File):$($_.Line)] $($_.Description)" 
        }
    }
    
    if ($low.Count -gt 0) {
        Write-Host "`n🟢 Low Severity Issues ($($low.Count)):"
        $low | ForEach-Object { 
            Write-Host "  [$($_.File):$($_.Line)] $($_.Description)" 
        }
    }
    
    $hasErrors = ($critical.Count -gt 0 -or $high.Count -gt 0)
    $hasWarnings = ($medium.Count -gt 0 -or $low.Count -gt 0)
    
    if ($hasErrors) {
        Write-Host "`n💥 CRITICAL ISSUES FOUND - Must be resolved before deployment"
        exit 1
    } elseif ($hasWarnings -and $FailOnWarning) {
        Write-Host "`n⚠️  WARNINGS FOUND - Review required"
        exit 1
    } else {
        Write-Host "`n⚠️  Issues found but not critical - Please review"
        exit 0
    }
} else {
    Write-Host "`n✅ No security issues detected"
    exit 0
}
