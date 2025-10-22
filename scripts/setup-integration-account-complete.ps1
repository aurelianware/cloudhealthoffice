param(
    [string]$ResourceGroup = "rg-hipaa-logic-apps",
    [string]$IntegrationAccountName = "hipaa-attachments-ia",
    [string]$Location = "eastus",
    [string]$SchemaFolder = "./schemas"
)

# --- Create Trading Partners ---
# Availity
$availityContent = @{
    b2b = @{
        businessIdentities = @(
            @{ qualifier = "ZZ"; value = "030240928" }
        )
    }
} | ConvertTo-Json -Depth 10

az logic integration-account partner create `
    --resource-group $ResourceGroup `
    --integration-account $IntegrationAccountName `
    --name "Availity" `
    --partner-type "B2B" `
    --content $availityContent

# QNXT (PCHP)
$pchpContent = @{
    b2b = @{
        businessIdentities = @(
            @{ qualifier = "ZZ"; value = "66917" }
        )
    }
} | ConvertTo-Json -Depth 10

az logic integration-account partner create `
    --resource-group $ResourceGroup `
    --integration-account $IntegrationAccountName `
    --name "PCHP-QNXT" `
    --partner-type "B2B" `
    --content $pchpContent

# --- Upload Schemas ---
$schemas = @(
    @{ name = "X12_005010X210_275"; file = "$SchemaFolder/Companion_275AttachmentEnvelope.xsd"; type = "Xml" },
    @{ name = "X12_005010X212_277"; file = "$SchemaFolder/X12_005010X212_277.xsd"; type = "X12" },
    @{ name = "X12_005010X217_278"; file = "$SchemaFolder/X12_005010X217_278.xsd"; type = "X12" }
)

foreach ($schema in $schemas) {
    if (Test-Path $schema.file) {
        az logic integration-account schema create `
            --resource-group $ResourceGroup `
            --integration-account-name $IntegrationAccountName `
            --name $schema.name `
            --schema-type $schema.type `
            --content @$schema.file
        Write-Host "✅ Uploaded schema: $($schema.name)"
    } else {
        Write-Warning "Schema file not found: $($schema.file)"
    }
}

# --- Create Agreements ---
# 275 Receive
az logic integration-account agreement create `
    --resource-group $ResourceGroup `
    --integration-account-name $IntegrationAccountName `
    --name "Availity-to-PCHP-275-Receive" `
    --agreement-type X12 `
    --host-partner "PCHP-QNXT" `
    --guest-partner "Availity" `
    --host-identity '{ "qualifier": "ZZ", "value": "66917" }' `
    --guest-identity '{ "qualifier": "ZZ", "value": "030240928" }' `
    --content '{
        "protocolSettings": {
            "x12": {
                "receiveSettings": {
                    "acknowledgementSettings": {
                        "needTechnicalAcknowledgement": true,
                        "batchTechnicalAcknowledgements": false,
                        "needFunctionalAcknowledgement": true,
                        "batchFunctionalAcknowledgements": false
                    },
                    "envelopeSettings": {
                        "controlStandardsId": "U",
                        "messageVersion": "005010X210"
                    }
                }
            }
        }
    }'

# 277 Send
az logic integration-account agreement create `
    --resource-group $ResourceGroup `
    --integration-account-name $IntegrationAccountName `
    --name "PCHP-to-Availity-277-Send" `
    --agreement-type X12 `
    --host-partner "PCHP-QNXT" `
    --guest-partner "Availity" `
    --host-identity '{ "qualifier": "ZZ", "value": "66917" }' `
    --guest-identity '{ "qualifier": "ZZ", "value": "030240928" }' `
    --content '{
        "protocolSettings": {
            "x12": {
                "sendSettings": {
                    "acknowledgementSettings": {
                        "needTechnicalAcknowledgement": true,
                        "batchTechnicalAcknowledgements": false,
                        "needFunctionalAcknowledgement": true,
                        "batchFunctionalAcknowledgements": false
                    },
                    "envelopeSettings": {
                        "controlStandardsId": "U",
                        "messageVersion": "005010X212"
                    }
                }
            }
        }
    }'

# 278 Internal
az logic integration-account agreement create `
    --resource-group $ResourceGroup `
    --integration-account-name $IntegrationAccountName `
    --name "PCHP-278-Processing" `
    --agreement-type X12 `
    --host-partner "PCHP-QNXT" `
    --guest-partner "PCHP-QNXT" `
    --host-identity '{ "qualifier": "ZZ", "value": "66917" }' `
    --guest-identity '{ "qualifier": "ZZ", "value": "66917" }' `
    --content '{
        "protocolSettings": {
            "x12": {
                "receiveSettings": {
                    "envelopeSettings": {
                        "controlStandardsId": "U",
                        "messageVersion": "005010X217"
                    }
                }
            }
        }
    }'

Write-Host "✅ Integration Account setup complete!"