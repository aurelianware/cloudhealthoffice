param(
    [string]$ResourceGroup = "rg-hipaa-logic-apps",
    [string]$IntegrationAccountName = "hipaa-attachments-ia",
    [string]$Location = "westus"
)

# Agreement 1: Clearinghouse-to-Health Plan-275-Receive
az logic integration-account agreement create `
    --resource-group $ResourceGroup `
    --integration-account-name $IntegrationAccountName `
    --name "Clearinghouse-to-Health Plan-275-Receive" `
    --agreement-type X12 `
    --host-partner "Health Plan Backend" `
    --guest-partner "Clearinghouse" `
    --host-identity "{ \"qualifier\": \"ZZ\", \"value\": \"{config.payerId}\" }" `
    --guest-identity "{ \"qualifier\": \"ZZ\", \"value\": \"030240928\" }" `
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
                        "messageVersion": "005010X215"
                    }
                }
            }
        }
    }'

# Agreement 2: Health Plan-to-Clearinghouse-277-Send
az logic integration-account agreement create `
    --resource-group $ResourceGroup `
    --integration-account-name $IntegrationAccountName `
    --name "Health Plan-to-Clearinghouse-277-Send" `
    --agreement-type X12 `
    --host-partner "Health Plan Backend" `
    --guest-partner "Clearinghouse" `
    --host-identity "{ \"qualifier\": \"ZZ\", \"value\": \"{config.payerId}\" }" `
    --guest-identity "{ \"qualifier\": \"ZZ\", \"value\": \"030240928\" }" `
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

# Agreement 3: Health Plan-278-Processing (internal)
az logic integration-account agreement create `
    --resource-group $ResourceGroup `
    --integration-account-name $IntegrationAccountName `
    --name "Health Plan-278-Processing" `
    --agreement-type X12 `
    --host-partner "Health Plan Backend" `
    --guest-partner "Health Plan Backend" `
    --host-identity "{ \"qualifier\": \"ZZ\", \"value\": \"{config.payerId}\" }" `
    --guest-identity "{ \"qualifier\": \"ZZ\", \"value\": \"{config.payerId}\" }" `
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

Write-Host "✅ X12 Agreements configured in Integration Account: $IntegrationAccountName"