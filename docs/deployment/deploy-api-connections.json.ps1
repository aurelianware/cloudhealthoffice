# Deploy API Connections from connections.json

param(
    [string]$ResourceGroup = "rg-hipaa-logic-apps",
    [string]$Location = "westus",
    [string]$ConnectionsFile = "$PSScriptRoot/logicapps/connections.json"
)

# Load connections.json
$connections = Get-Content $ConnectionsFile | ConvertFrom-Json

foreach ($name in $connections.managedApiConnections.PSObject.Properties.Name) {
    $conn = $connections.managedApiConnections.$name
    $apiId = $conn.api.id
    $connId = $conn.connection.id
    $connName = $name

    Write-Host "Deploying API connection: $connName"

    # Example for SFTP (update with your credentials)
    if ($connName -eq "sftp-ssh") {
        az web connection create sftp-ssh `
            --resource-group $ResourceGroup `
            --name $connName `
            --location $Location `
            --host "sftp.example.com" `
            --username "logicapp" `
            --password "<your-password>"
    }
    # Example for Azure Blob (update with your account/key)
    elseif ($connName -eq "azureblob") {
        az web connection create azureblob `
            --resource-group $ResourceGroup `
            --name $connName `
            --location $Location `
            --account-name "<your-storage-account>" `
            --access-key "<your-access-key>"
    }
    # Example for Service Bus (update with your connection string)
    elseif ($connName -eq "servicebus") {
        az web connection create servicebus `
            --resource-group $ResourceGroup `
            --name $connName `
            --location $Location `
            --connection-string "<your-servicebus-connection-string>"
    }
    # Example for Integration Account (update with your resource ID)
    elseif ($connName -eq "integrationaccount") {
        az web connection create integrationaccount `
            --resource-group $ResourceGroup `
            --name $connName `
            --location $Location `
            --integration-account-id "<your-integration-account-resource-id>"
    }
    else {
        Write-Warning "Unknown connection type for $connName. Please add logic for this type."
    }
}

Write-Host "✅ API connections deployment script completed."