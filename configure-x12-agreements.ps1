param(
    [string]$ResourceGroup = "rg-hipaa-logic-apps",
    [string]$IntegrationAccountName = "hipaa-attachments-ia",
    [string]$Location = "westus"
)

# Agreement 1: Availity-to-EXHP-275-Receive
az logic integration-account agreement create `
    --resource-group $ResourceGroup `
    --integration-account-name $IntegrationAccountName `
    --name "Availity-to-EXHP-275-Receive" `
    --agreement-type X12 `
    --host-partner "EXHP-QNXT" `
    --guest-partner "Availity" `
    --host-identity "{ \"qualifier\": \"ZZ\", \"value\": \"66917\" }" `
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

# Agreement 2: EXHP-to-Availity-277-Send
az logic integration-account agreement create `
    --resource-group $ResourceGroup `
    --integration-account-name $IntegrationAccountName `
    --name "EXHP-to-Availity-277-Send" `
    --agreement-type X12 `
    --host-partner "EXHP-QNXT" `
    --guest-partner "Availity" `
    --host-identity "{ \"qualifier\": \"ZZ\", \"value\": \"66917\" }" `
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

# Agreement 3: EXHP-278-Processing (internal)
az logic integration-account agreement create `
    --resource-group $ResourceGroup `
    --integration-account-name $IntegrationAccountName `
    --name "EXHP-278-Processing" `
    --agreement-type X12 `
    --host-partner "EXHP-QNXT" `
    --guest-partner "EXHP-QNXT" `
    --host-identity "{ \"qualifier\": \"ZZ\", \"value\": \"66917\" }" `
    --guest-identity "{ \"qualifier\": \"ZZ\", \"value\": \"66917\" }" `
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