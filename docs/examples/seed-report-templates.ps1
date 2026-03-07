#!/usr/bin/env pwsh

<#
    .SYNOPSIS
    Seeds example report templates into the Meduza API
    
    .DESCRIPTION
    Creates 5 predefined report templates for Software Inventory, Logs, 
    Configuration Audit, Tickets, and Agent Hardware datasets.
    
    .PARAMETER ApiUrl
    Base URL of the Meduza API (default: http://localhost:5000)
    
    .PARAMETER ClientId
    Client GUID to associate templates with (default: empty for system-wide templates)
#>

param(
    [string]$ApiUrl = "http://localhost:5000",
    [string]$ClientId = "",
    [string]$CreatedBy = "system-seed"
)

$ErrorActionPreference = "Stop"

function Invoke-ApiRequest {
    param(
        [string]$Method,
        [string]$Endpoint,
        [object]$Body
    )
    
    $url = "$ApiUrl/api/reports/$Endpoint"
    $headers = @{
        "Content-Type" = "application/json"
    }
    
    $bodyJson = $Body | ConvertTo-Json -Depth 10
    Write-Host "POST $url"
    Write-Host "Body: $bodyJson`n"
    
    try {
        $response = Invoke-WebRequest -Uri $url -Method $Method -Headers $headers -Body $bodyJson
        Write-Host "✅ Success: $($response.StatusCode)" -ForegroundColor Green
        return $response.Content | ConvertFrom-Json
    }
    catch {
        Write-Host "❌ Error: $($_.Exception.Message)" -ForegroundColor Red
        if ($_.Exception.Response) {
            $streamReader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
            $errorContent = $streamReader.ReadToEnd()
            Write-Host "Response: $errorContent" -ForegroundColor Red
        }
        return $null
    }
}

Write-Host "🚀 Seeding Report Templates to $ApiUrl" -ForegroundColor Cyan
Write-Host "================================================`n"

# Template 1: Software Inventory Report
$softwareTemplate = @{
    ClientId = if ($ClientId) { [guid]$ClientId } else { $null }
    Name = "Software Inventory - All Clients"
    Description = "Comprehensive list of installed software across all agents, including version and publisher information"
    DatasetType = "SoftwareInventory"
    DefaultFormat = "Xlsx"
    LayoutJson = @{
        title = "Software Inventory Report"
        columns = @(
            @{ header = "Client"; field = "clientId"; width = 20 }
            @{ header = "Site"; field = "siteId"; width = 20 }
            @{ header = "Agent"; field = "agentId"; width = 20 }
            @{ header = "Software"; field = "softwareName"; width = 30 }
            @{ header = "Publisher"; field = "publisher"; width = 25 }
            @{ header = "Version"; field = "version"; width = 15 }
            @{ header = "Installed"; field = "installedAt"; width = 20; format = "datetime" }
        )
        pageSize = 100
        orientation = "landscape"
    } | ConvertTo-Json -Compress
    FiltersJson = @{
        limit = 5000
        orderBy = "installedAt"
        orderDirection = "DESC"
    } | ConvertTo-Json -Compress
    CreatedBy = $CreatedBy
}

Write-Host "1️⃣  Creating: Software Inventory Template"
$result1 = Invoke-ApiRequest -Method "POST" -Endpoint "templates" -Body $softwareTemplate
Write-Host ""

# Template 2: System Logs Report
$logsTemplate = @{
    ClientId = if ($ClientId) { [guid]$ClientId } else { $null }
    Name = "System Logs - Last 7 Days"
    Description = "Recent system logs filtered by error and warning levels, useful for troubleshooting and compliance audits"
    DatasetType = "Logs"
    DefaultFormat = "Xlsx"
    LayoutJson = @{
        title = "System Logs Report"
        columns = @(
            @{ header = "Client"; field = "clientId"; width = 20 }
            @{ header = "Site"; field = "siteId"; width = 20 }
            @{ header = "Agent"; field = "agentId"; width = 20 }
            @{ header = "Type"; field = "type"; width = 15 }
            @{ header = "Level"; field = "level"; width = 12 }
            @{ header = "Source"; field = "source"; width = 20 }
            @{ header = "Message"; field = "message"; width = 50 }
            @{ header = "Timestamp"; field = "timestamp"; width = 20; format = "datetime" }
        )
        pageSize = 1000
    } | ConvertTo-Json -Compress
    FiltersJson = @{
        level = @("Error", "Warning")
        daysBack = 7
        limit = 10000
        orderBy = "timestamp"
        orderDirection = "DESC"
    } | ConvertTo-Json -Compress
    CreatedBy = $CreatedBy
}

Write-Host "2️⃣  Creating: System Logs Template"
$result2 = Invoke-ApiRequest -Method "POST" -Endpoint "templates" -Body $logsTemplate
Write-Host ""

# Template 3: Configuration Audit Report
$auditTemplate = @{
    ClientId = if ($ClientId) { [guid]$ClientId } else { $null }
    Name = "Configuration Changes - Monthly"
    Description = "Tracks all configuration modifications for compliance and change management purposes"
    DatasetType = "ConfigurationAudit"
    DefaultFormat = "Xlsx"
    LayoutJson = @{
        title = "Configuration Audit Report"
        columns = @(
            @{ header = "Entity Type"; field = "entityType"; width = 20 }
            @{ header = "Entity ID"; field = "entityId"; width = 20 }
            @{ header = "Field Changed"; field = "fieldName"; width = 25 }
            @{ header = "Old Value"; field = "oldValue"; width = 25 }
            @{ header = "New Value"; field = "newValue"; width = 25 }
            @{ header = "Changed By"; field = "changedBy"; width = 20 }
            @{ header = "Changed At"; field = "changedAt"; width = 20; format = "datetime" }
            @{ header = "Reason"; field = "reason"; width = 30 }
        )
        pageSize = 500
    } | ConvertTo-Json -Compress
    FiltersJson = @{
        daysBack = 30
        limit = 10000
        orderBy = "changedAt"
        orderDirection = "DESC"
    } | ConvertTo-Json -Compress
    CreatedBy = $CreatedBy
}

Write-Host "3️⃣  Creating: Configuration Audit Template"
$result3 = Invoke-ApiRequest -Method "POST" -Endpoint "templates" -Body $auditTemplate
Write-Host ""

# Template 4: Tickets/Tickets Report
$ticketsTemplate = @{
    ClientId = if ($ClientId) { [guid]$ClientId } else { $null }
    Name = "Open Tickets - Priority View"
    Description = "Overview of open tickets sorted by priority and SLA status, ideal for incident management dashboards"
    DatasetType = "Tickets"
    DefaultFormat = "Xlsx"
    LayoutJson = @{
        title = "Tickets Report"
        columns = @(
            @{ header = "Client"; field = "clientId"; width = 20 }
            @{ header = "Site"; field = "siteId"; width = 20 }
            @{ header = "Agent"; field = "agentId"; width = 20 }
            @{ header = "Status"; field = "workflowStateId"; width = 15 }
            @{ header = "Priority"; field = "priority"; width = 12 }
            @{ header = "Created"; field = "createdAt"; width = 18; format = "datetime" }
            @{ header = "Closed"; field = "closedAt"; width = 18; format = "datetime" }
            @{ header = "SLA Breached"; field = "slaBreached"; width = 12 }
        )
        pageSize = 500
    } | ConvertTo-Json -Compress
    FiltersJson = @{
        status = @("Open", "InProgress")
        limit = 5000
        orderBy = "priority"
        orderDirection = "ASC"
    } | ConvertTo-Json -Compress
    CreatedBy = $CreatedBy
}

Write-Host "4️⃣  Creating: Tickets Template"
$result4 = Invoke-ApiRequest -Method "POST" -Endpoint "templates" -Body $ticketsTemplate
Write-Host ""

# Template 5: Agent Hardware Report
$hardwareTemplate = @{
    ClientId = if ($ClientId) { [guid]$ClientId } else { $null }
    Name = "Agent Hardware Inventory"
    Description = "Current hardware specifications of all agents including OS, CPU, and memory resources"
    DatasetType = "AgentHardware"
    DefaultFormat = "Xlsx"
    LayoutJson = @{
        title = "Agent Hardware Inventory"
        columns = @(
            @{ header = "Client"; field = "clientId"; width = 20 }
            @{ header = "Site"; field = "siteId"; width = 20 }
            @{ header = "Agent"; field = "agentId"; width = 20 }
            @{ header = "OS Name"; field = "osName"; width = 25 }
            @{ header = "Processor"; field = "processor"; width = 35 }
            @{ header = "Memory (GB)"; field = "totalMemoryGB"; width = 15 }
            @{ header = "Collected"; field = "collectedAt"; width = 20; format = "datetime" }
        )
        pageSize = 500
    } | ConvertTo-Json -Compress
    FiltersJson = @{
        limit = 10000
        orderBy = "collectedAt"
        orderDirection = "DESC"
    } | ConvertTo-Json -Compress
    CreatedBy = $CreatedBy
}

Write-Host "5️⃣  Creating: Agent Hardware Template"
$result5 = Invoke-ApiRequest -Method "POST" -Endpoint "templates" -Body $hardwareTemplate
Write-Host ""

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "✅ Seeding complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Created templates:" -ForegroundColor Yellow
if ($result1) { Write-Host "  1. Software Inventory (ID: $($result1.id))" }
if ($result2) { Write-Host "  2. System Logs (ID: $($result2.id))" }
if ($result3) { Write-Host "  3. Configuration Audit (ID: $($result3.id))" }
if ($result4) { Write-Host "  4. Tickets (ID: $($result4.id))" }
if ($result5) { Write-Host "  5. Agent Hardware (ID: $($result5.id))" }

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  • View templates: GET $ApiUrl/api/reports/templates"
Write-Host "  • Run a report: POST $ApiUrl/api/reports/run"
Write-Host "  • Check status: GET $ApiUrl/api/reports/executions/{executionId}"
