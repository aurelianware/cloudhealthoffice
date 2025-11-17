<#
.SYNOPSIS
    Validates X12 EDI files for HIPAA 275/277/278 format compliance.

.DESCRIPTION
    This script validates X12 EDI file structure and content:
    - ISA/IEA envelope segments
    - GS/GE functional group segments
    - ST/SE transaction set segments
    - Trading partner identifiers (Availity/PCHP)
    - Segment structure and delimiters
    - Transaction types (275/277/278)

.PARAMETER Path
    Path to the EDI file to validate. Can be a single file or directory.

.PARAMETER TransactionType
    Expected transaction type (275, 277, or 278). If not specified, will detect from file.

.PARAMETER Strict
    Enable strict validation mode with additional checks.

.EXAMPLE
    .\validate-edi-x12.ps1 -Path "test-x12-275-availity-to-pchp.edi"
    Validates a single EDI file.

.EXAMPLE
    .\validate-edi-x12.ps1 -Path "." -TransactionType 275 -Strict
    Validates all EDI files in current directory with strict checking.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true, HelpMessage="Path to EDI file or directory")]
    [ValidateNotNullOrEmpty()]
    [string]$Path,
    
    [Parameter(Mandatory=$false, HelpMessage="Expected transaction type (275/277/278)")]
    [ValidateSet('275', '277', '278', $null)]
    [string]$TransactionType = $null,
    
    [Parameter(Mandatory=$false, HelpMessage="Enable strict validation mode")]
    [switch]$Strict
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# HIPAA X12 constants
$VALID_ISA_QUALIFIERS = @('ZZ', '01', '14', '20', '27', '28', '29', '30')
$AVAILITY_ID = '030240928'
$PCHP_ID = '66917'

# Transaction type identifiers
$TRANSACTION_TYPES = @{
    '275' = 'Attachment Request'
    '277' = 'Status Response'
    '278' = 'Health Care Services Review'
}

# Validation results
$script:ValidationErrors = @()
$script:ValidationWarnings = @()
$script:ValidationInfo = @()

function Write-ValidationError {
    param([string]$Message, [string]$File = "", [int]$Line = 0)
    $script:ValidationErrors += @{
        Message = $Message
        File = $File
        Line = $Line
        Severity = "Error"
    }
    Write-Host "::error file=$File,line=$Line::$Message"
}

function Write-ValidationWarning {
    param([string]$Message, [string]$File = "", [int]$Line = 0)
    $script:ValidationWarnings += @{
        Message = $Message
        File = $File
        Line = $Line
        Severity = "Warning"
    }
    Write-Host "::warning file=$File,line=$Line::$Message"
}

function Write-ValidationInfo {
    param([string]$Message)
    $script:ValidationInfo += $Message
    Write-Host "ℹ️  $Message"
}

function Test-EdiSegment {
    param(
        [string]$Segment,
        [string]$ExpectedSegmentId,
        [string]$File,
        [int]$Line
    )
    
    if (-not $Segment.StartsWith($ExpectedSegmentId)) {
        Write-ValidationError "Expected $ExpectedSegmentId segment but got: $($Segment.Substring(0, [Math]::Min(10, $Segment.Length)))" $File $Line
        return $false
    }
    return $true
}

function Test-ISASegment {
    param(
        [string]$Segment,
        [string]$File,
        [int]$Line
    )
    
    # ISA segment should be exactly 106 characters (including segment terminator)
    if ($Segment.Length -lt 100) {
        Write-ValidationError "ISA segment is too short (expected ~106 characters, got $($Segment.Length))" $File $Line
        return $false
    }
    
    # Parse ISA elements (using * as separator in most cases)
    $elements = $Segment -split '\*'
    
    if ($elements.Count -lt 16) {
        Write-ValidationError "ISA segment has insufficient elements (expected 16+, got $($elements.Count))" $File $Line
        return $false
    }
    
    # Validate sender/receiver qualifiers
    $senderQual = $elements[5]
    $receiverQual = $elements[7]
    
    if ($VALID_ISA_QUALIFIERS -notcontains $senderQual) {
        Write-ValidationWarning "ISA sender qualifier '$senderQual' is not a standard HIPAA qualifier" $File $Line
    }
    
    if ($VALID_ISA_QUALIFIERS -notcontains $receiverQual) {
        Write-ValidationWarning "ISA receiver qualifier '$receiverQual' is not a standard HIPAA qualifier" $File $Line
    }
    
    # Validate trading partner IDs
    $senderId = $elements[6].Trim()
    $receiverId = $elements[8].Trim()
    
    $validPairs = @(
        @($AVAILITY_ID, $PCHP_ID),
        @($PCHP_ID, $AVAILITY_ID)
    )
    
    $found = $false
    foreach ($pair in $validPairs) {
        if ($senderId -eq $pair[0] -and $receiverId -eq $pair[1]) {
            $found = $true
            Write-ValidationInfo "Trading partners validated: $senderId -> $receiverId"
            break
        }
    }
    
    if (-not $found -and $Strict) {
        Write-ValidationWarning "Trading partner IDs don't match expected Availity/PCHP pairs: $senderId -> $receiverId" $File $Line
    }
    
    return $true
}

function Test-GSSegment {
    param(
        [string]$Segment,
        [string]$File,
        [int]$Line
    )
    
    $elements = $Segment -split '\*'
    
    if ($elements.Count -lt 8) {
        Write-ValidationError "GS segment has insufficient elements (expected 8+, got $($elements.Count))" $File $Line
        return $false
    }
    
    # GS01 - Functional Identifier Code (should be 'HI' for HIPAA)
    $funcId = $elements[1]
    if ($funcId -ne 'HI') {
        Write-ValidationWarning "GS functional identifier is '$funcId' (expected 'HI' for HIPAA)" $File $Line
    }
    
    return $true
}

function Test-STSegment {
    param(
        [string]$Segment,
        [string]$File,
        [int]$Line,
        [string]$ExpectedType = $null
    )
    
    $elements = $Segment -split '\*'
    
    if ($elements.Count -lt 3) {
        Write-ValidationError "ST segment has insufficient elements (expected 3+, got $($elements.Count))" $File $Line
        return $null
    }
    
    $transactionType = $elements[1]
    
    if (-not $TRANSACTION_TYPES.ContainsKey($transactionType)) {
        Write-ValidationError "Unknown transaction type '$transactionType' (expected 275, 277, or 278)" $File $Line
        return $null
    }
    
    if ($ExpectedType -and $transactionType -ne $ExpectedType) {
        Write-ValidationError "Transaction type mismatch: expected $ExpectedType but got $transactionType" $File $Line
        return $null
    }
    
    Write-ValidationInfo "Detected transaction type: $transactionType ($($TRANSACTION_TYPES[$transactionType]))"
    
    return $transactionType
}

function Test-EdiFile {
    param(
        [string]$FilePath
    )
    
    Write-Host "`n📄 Validating: $FilePath"
    
    # Reset validation arrays for this file to prevent accumulation across multiple files
    $script:ValidationErrors = @()
    $script:ValidationWarnings = @()
    $script:ValidationInfo = @()
    
    if (-not (Test-Path $FilePath)) {
        Write-ValidationError "File not found: $FilePath" $FilePath 0
        return $false
    }
    
    # Read file content
    $content = Get-Content -Path $FilePath -Raw
    
    if ([string]::IsNullOrWhiteSpace($content)) {
        Write-ValidationError "File is empty" $FilePath 0
        return $false
    }
    
    # Remove all line breaks (EDI files may have them for readability)
    $content = $content -replace "`r`n", "" -replace "`n", "" -replace "`r", ""
    
    # Split into segments (using ~ as segment terminator)
    $segments = $content -split '~' | Where-Object { $_.Trim() -ne '' }
    
    if ($segments.Count -eq 0) {
        Write-ValidationError "No EDI segments found in file" $FilePath 0
        return $false
    }
    
    Write-ValidationInfo "Found $($segments.Count) segments"
    
    # Validate envelope structure
    $lineNum = 1
    
    # ISA segment (must be first)
    if (-not (Test-ISASegment $segments[0] $FilePath $lineNum)) {
        return $false
    }
    $lineNum++
    
    # GS segment (must be second)
    if (-not (Test-EdiSegment $segments[1] "GS" $FilePath $lineNum)) {
        return $false
    }
    if (-not (Test-GSSegment $segments[1] $FilePath $lineNum)) {
        return $false
    }
    $lineNum++
    
    # ST segment (must be third)
    if (-not (Test-EdiSegment $segments[2] "ST" $FilePath $lineNum)) {
        return $false
    }
    $detectedType = Test-STSegment $segments[2] $FilePath $lineNum $TransactionType
    if (-not $detectedType) {
        return $false
    }
    $lineNum++
    
    # Find closing segments
    $lastSegments = $segments[-3..-1]
    
    # SE segment (transaction set trailer)
    if (-not $lastSegments[0].StartsWith("SE")) {
        Write-ValidationError "Missing SE (transaction set trailer) segment" $FilePath ($segments.Count - 2)
    }
    
    # GE segment (functional group trailer)
    if (-not $lastSegments[1].StartsWith("GE")) {
        Write-ValidationError "Missing GE (functional group trailer) segment" $FilePath ($segments.Count - 1)
    }
    
    # IEA segment (interchange control trailer)
    if (-not $lastSegments[2].StartsWith("IEA")) {
        Write-ValidationError "Missing IEA (interchange control trailer) segment" $FilePath $segments.Count
    }
    
    # Validate segment count in SE
    $seElements = $lastSegments[0] -split '\*'
    if ($seElements.Count -ge 2) {
        $segmentCount = [int]$seElements[1]
        # SE01 should equal the number of segments from ST to SE (inclusive)
        # Find the index of ST and SE segments
        $stIndex = 2  # ST is always the 3rd segment (index 2)
        $seIndex = $segments.Count - 3  # SE is 3rd from the end
        $actualCount = $seIndex - $stIndex + 1
        if ($segmentCount -ne $actualCount) {
            Write-ValidationWarning "SE segment count mismatch: claims $segmentCount segments, but found $actualCount" $FilePath ($segments.Count - 2)
        }
    }
    
    return $script:ValidationErrors.Count -eq 0
}

function Test-EdiDirectory {
    param(
        [string]$DirectoryPath
    )
    
    $ediFiles = Get-ChildItem -Path $DirectoryPath -Filter "*.edi" -File -Recurse
    
    if ($ediFiles.Count -eq 0) {
        Write-Host "⚠️  No .edi files found in directory: $DirectoryPath"
        return $true
    }
    
    Write-Host "`n🔍 Found $($ediFiles.Count) EDI file(s) to validate"
    
    $allPassed = $true
    foreach ($file in $ediFiles) {
        $passed = Test-EdiFile $file.FullName
        if (-not $passed) {
            $allPassed = $false
        }
    }
    
    return $allPassed
}

# Main execution
Write-Host "🏥 HIPAA X12 EDI Validator"
Write-Host "=========================="

$targetPath = Resolve-Path $Path -ErrorAction SilentlyContinue
if (-not $targetPath) {
    Write-Host "::error::Path not found: $Path"
    exit 1
}

$isDirectory = Test-Path $targetPath -PathType Container
$allPassed = $true

if ($isDirectory) {
    $allPassed = Test-EdiDirectory $targetPath
} else {
    $allPassed = Test-EdiFile $targetPath
}

# Summary
Write-Host "`n" + ("=" * 50)
Write-Host "📊 Validation Summary"
Write-Host ("=" * 50)

if ($script:ValidationInfo.Count -gt 0) {
    Write-Host "`nℹ️  Information ($($script:ValidationInfo.Count)):"
    $script:ValidationInfo | ForEach-Object { Write-Host "  $_" }
}

if ($script:ValidationWarnings.Count -gt 0) {
    Write-Host "`n⚠️  Warnings ($($script:ValidationWarnings.Count)):"
    $script:ValidationWarnings | ForEach-Object { 
        Write-Host "  [$($_.File):$($_.Line)] $($_.Message)" 
    }
}

if ($script:ValidationErrors.Count -gt 0) {
    Write-Host "`n❌ Errors ($($script:ValidationErrors.Count)):"
    $script:ValidationErrors | ForEach-Object { 
        Write-Host "  [$($_.File):$($_.Line)] $($_.Message)" 
    }
    Write-Host "`n💥 Validation FAILED"
    exit 1
}

if ($allPassed) {
    Write-Host "`n✅ All validations PASSED"
    exit 0
} else {
    Write-Host "`n💥 Validation FAILED"
    exit 1
}
