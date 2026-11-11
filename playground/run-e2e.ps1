param(
    [string]$Node1Url = "",
    [string]$Node2Url = "",
    [switch]$RunIntegrationTestProject
)

$ErrorActionPreference = "Stop"

Write-Host @"
========================================================================
   _  _      _          _     _  ___           _          ___  _           
  | || |_  _| |__  _ _ (_) __| |/ __|__ _  __ | |_   ___ | _ \| |_  _ ___ 
  | __ | || | '_ \| '_|| |/ _` | (__/ _` |/ _|| ' \ / -_)|  _/| | || (_-< 
  |_||_|\_, |_.__/|_|  |_|\__,_|\___\__,_|\__||_||_|\___||_|  |_|\_,_/__/ 
        |__/                                                              
               End-to-End Aspire Distributed Test Suite
========================================================================
"@ -ForegroundColor Cyan

if ($RunIntegrationTestProject -or ([string]::IsNullOrEmpty($Node1Url) -and [string]::IsNullOrEmpty($Node2Url))) {
    Write-Host "`n[1/2] Building and executing automated Aspire.Hosting.Testing test suite..." -ForegroundColor Yellow
    
    dotnet build "$PSScriptRoot/AspirePlayground.Tests/AspirePlayground.Tests.csproj" -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    $testExe = "$PSScriptRoot/AspirePlayground.Tests/bin/Debug/net10.0/AspirePlayground.Tests.exe"
    & $testExe -showLiveOutput
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        Write-Host "`nAll Aspire E2E integration tests PASSED successfully!" -ForegroundColor Green
    } else {
        Write-Host "`nIntegration tests encountered errors (Exit Code: $exitCode)." -ForegroundColor Red
    }
    exit $exitCode
}

# Interactive mode against running nodes
Write-Host "Target Node 1: $Node1Url" -ForegroundColor Cyan
Write-Host "Target Node 2: $Node2Url" -ForegroundColor Cyan
Write-Host "Starting Scenario Verification...`n" -ForegroundColor Yellow

# Helper function
function Invoke-TestStep($name, [scriptblock]$action) {
    Write-Host -NoNewline "  - $name... "
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $action
        $sw.Stop()
        Write-Host "OK ($($sw.ElapsedMilliseconds) ms)" -ForegroundColor Green
    } catch {
        $sw.Stop()
        Write-Host "FAILED ($($sw.ElapsedMilliseconds) ms)" -ForegroundColor Red
        Write-Host "    Error: $_" -ForegroundColor Red
        throw
    }
}

# Scenario 1: Cold Start & L1 Hit
Invoke-TestStep "Scenario 1: Cold start MISS and subsequent L1 warm HIT on Node 1" {
    $res1 = Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/101" -Method Get
    if ($res1.source -notlike "*MISS*") { throw "Expected MISS on first call, got: $($res1.source)" }
    $queries1 = $res1.totalDbQueriesAcrossAllCalls

    $res2 = Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/101" -Method Get
    if ($res2.source -notlike "*HIT*") { throw "Expected HIT on second call, got: $($res2.source)" }
    $queries2 = $res2.totalDbQueriesAcrossAllCalls

    if ($queries1 -ne $queries2) { throw "Expected zero DB queries on second call, but query count changed." }
}

# Scenario 2: L2 Cross-Node Sharing
Invoke-TestStep "Scenario 2: Node 2 retrieves product from shared Redis L2 (Zero DB queries)" {
    Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/102" -Method Get | Out-Null

    # Query counters are per process: compare Node 2 with itself, before and after its read.
    $before = (Invoke-RestMethod -Uri "$Node2Url/api/system/status" -Method Get).totalDbQueries
    $res2 = Invoke-RestMethod -Uri "$Node2Url/api/products/tenant_alpha/102" -Method Get
    if ($res2.source -notlike "*HIT*") { throw "Expected L2 HIT on Node 2, got: $($res2.source)" }
    $after = (Invoke-RestMethod -Uri "$Node2Url/api/system/status" -Method Get).totalDbQueries

    if ($before -ne $after) { throw "Node 2 triggered a DB query instead of reading from Redis L2!" }
}

# Scenario 3: Stampede Prevention
Invoke-TestStep "Scenario 3: Stampede concurrency protection (100 concurrent requests, exactly 1 DB query)" {
    Invoke-RestMethod -Uri "$Node1Url/api/system/reset-db-counter" -Method Post | Out-Null

    $productId = 99999

    # PowerShell script blocks cannot run on thread-pool threads (no runspace), so the concurrency comes from HttpClient.
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object System.Net.Http.HttpClient
    try {
        $tasks = [System.Threading.Tasks.Task[]](1..100 | ForEach-Object {
            $client.GetAsync("$Node1Url/api/products/tenant_alpha/$productId")
        })
        [System.Threading.Tasks.Task]::WaitAll($tasks)
        $failed = @($tasks | Where-Object { -not $_.Result.IsSuccessStatusCode }).Count
        if ($failed -gt 0) { throw "$failed of 100 concurrent requests failed." }
    } finally {
        $client.Dispose()
    }

    $status = Invoke-RestMethod -Uri "$Node1Url/api/system/status" -Method Get
    if ($status.totalDbQueries -ne 1) {
        throw "Stampede protection failed! Expected exactly 1 DB query, but got: $($status.totalDbQueries)"
    }
}

# Scenario 4: Cross-pod Real-time Eviction
Invoke-TestStep "Scenario 4: Cross-pod Redis backplane invalidation (Node 1 mutates -> Node 2 evicts L1)" {
    $productId = 301
    Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Get | Out-Null
    Invoke-RestMethod -Uri "$Node2Url/api/products/tenant_alpha/$productId" -Method Get | Out-Null

    $body = @{ Name = "CLI Realtime Update"; Price = 189.99 } | ConvertTo-Json
    Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Put -Body $body -ContentType "application/json" | Out-Null

    Start-Sleep -Milliseconds 400

    $node2Res = Invoke-RestMethod -Uri "$Node2Url/api/products/tenant_alpha/$productId" -Method Get
    if ($node2Res.product.name -ne "CLI Realtime Update") {
        throw "Node 2 served stale memory! Backplane eviction failed."
    }
}

# Scenario 5: CQRS Query & Command
Invoke-TestStep "Scenario 5: CQRS Complex Object Query & Command invalidation" {
    $qBody = @{ TenantId = "tenant_alpha"; ProductId = 701 } | ConvertTo-Json
    $res1 = Invoke-RestMethod -Uri "$Node1Url/api/products/query" -Method Post -Body $qBody -ContentType "application/json"
    if ($res1.source -notlike "*MISS*") { throw "Expected MISS on first CQRS query" }

    $cBody = @{ TenantId = "tenant_alpha"; ProductId = 701; Name = "CQRS Item from CLI"; Price = 55.55 } | ConvertTo-Json
    Invoke-RestMethod -Uri "$Node1Url/api/products/command" -Method Post -Body $cBody -ContentType "application/json" | Out-Null

    $res2 = Invoke-RestMethod -Uri "$Node1Url/api/products/query" -Method Post -Body $qBody -ContentType "application/json"
    if ($res2.product.name -ne "CQRS Item from CLI") {
        throw "CQRS command did not invalidate cached query properly!"
    }
}

# ---------------------------------------------------------------------
# Fail-safe scenarios (outage, latency and expiration are simulated per node)
# ---------------------------------------------------------------------

function Set-DbOutage($nodeUrl, [bool]$enabled) {
    Invoke-RestMethod -Uri "$nodeUrl/api/system/db-outage?enabled=$($enabled.ToString().ToLowerInvariant())" -Method Post | Out-Null
}

function Set-DbLatency($nodeUrl, [int]$ms) {
    Invoke-RestMethod -Uri "$nodeUrl/api/system/db-latency?ms=$ms" -Method Post | Out-Null
}

function Expire-Product($nodeUrl, $tenantId, $productId) {
    Invoke-RestMethod -Uri "$nodeUrl/api/system/expire/$tenantId/$productId" -Method Post | Out-Null
}

function Get-StatusCode($uri) {
    # Works on Windows PowerShell 5.1 and PowerShell 7: non-2xx responses surface as exceptions.
    try {
        return [int](Invoke-WebRequest -Uri $uri -UseBasicParsing).StatusCode
    } catch {
        if ($null -eq $_.Exception.Response) { throw }
        return [int]$_.Exception.Response.StatusCode
    }
}

# Scenario 6: Database outage -> last known value
Invoke-TestStep "Scenario 6: Fail-safe serves the last known value during a database outage (with throttled retries)" {
    $productId = 961
    $body = @{ Name = "CLI Fail-safe Lamp"; Price = 19.99 } | ConvertTo-Json
    Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Put -Body $body -ContentType "application/json" | Out-Null
    Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Get | Out-Null

    try {
        Set-DbOutage $Node1Url $true
        Expire-Product $Node1Url "tenant_alpha" $productId

        $res = Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Get
        if ($res.source -notlike "*FAIL-SAFE*") { throw "Expected FAIL-SAFE, got: $($res.source)" }
        if ($res.product.name -ne "CLI Fail-safe Lamp") { throw "Fail-safe served an unexpected value: $($res.product.name)" }

        $before = (Invoke-RestMethod -Uri "$Node1Url/api/system/status" -Method Get).totalDbQueries
        1..5 | ForEach-Object { Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Get | Out-Null }
        $after = (Invoke-RestMethod -Uri "$Node1Url/api/system/status" -Method Get).totalDbQueries
        if ($before -ne $after) { throw "Throttle failed: the failing database was queried $($after - $before) more times." }
    } finally {
        Set-DbOutage $Node1Url $false
    }
}

# Scenario 7: Database outage without backup -> 503
Invoke-TestStep "Scenario 7: Without a fail-safe backup, a database outage returns 503" {
    try {
        Set-DbOutage $Node1Url $true
        $code = Get-StatusCode "$Node1Url/api/products/tenant_alpha/962"
        if ($code -ne 503) { throw "Expected 503, got: $code" }
    } finally {
        Set-DbOutage $Node1Url $false
    }
}

# Scenario 8: Backup shared through Redis L2
Invoke-TestStep "Scenario 8: Node 2 serves the fail-safe backup stored in Redis L2 by Node 1" {
    $productId = 963
    $body = @{ Name = "CLI Shared Backup Chair"; Price = 89 } | ConvertTo-Json
    Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Put -Body $body -ContentType "application/json" | Out-Null
    Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Get | Out-Null

    try {
        Set-DbOutage $Node2Url $true
        Expire-Product $Node2Url "tenant_alpha" $productId

        $res = Invoke-RestMethod -Uri "$Node2Url/api/products/tenant_alpha/$productId" -Method Get
        if ($res.source -notlike "*FAIL-SAFE*") { throw "Expected FAIL-SAFE on Node 2, got: $($res.source)" }
        if ($res.product.name -ne "CLI Shared Backup Chair") { throw "Node 2 served an unexpected value: $($res.product.name)" }
    } finally {
        Set-DbOutage $Node2Url $false
    }
}

# Scenario 9: Invalidation removes the backup on every node
Invoke-TestStep "Scenario 9: A mutation on Node 1 removes the fail-safe backup on every node" {
    $productId = 964
    Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Get | Out-Null
    Invoke-RestMethod -Uri "$Node2Url/api/products/tenant_alpha/$productId" -Method Get | Out-Null

    $body = @{ Name = "CLI Changed Desk"; Price = 250 } | ConvertTo-Json
    Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Put -Body $body -ContentType "application/json" | Out-Null
    Start-Sleep -Milliseconds 500

    try {
        Set-DbOutage $Node2Url $true
        $code = Get-StatusCode "$Node2Url/api/products/tenant_alpha/$productId"
        if ($code -ne 503) { throw "Node 2 resurrected an invalidated value (expected 503, got: $code)." }
    } finally {
        Set-DbOutage $Node2Url $false
    }
}

# Scenario 10: Slow database -> soft timeout
Invoke-TestStep "Scenario 10: Soft timeout answers a slow database with the backup and refreshes in the background" {
    $productId = 965
    $body = @{ Name = "CLI Slow Sofa"; Price = 500 } | ConvertTo-Json
    Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Put -Body $body -ContentType "application/json" | Out-Null
    Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Get | Out-Null

    try {
        Set-DbLatency $Node1Url 3000
        Expire-Product $Node1Url "tenant_alpha" $productId

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $res = Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Get
        $sw.Stop()
        if ($res.source -notlike "*FAIL-SAFE*") { throw "Expected FAIL-SAFE, got: $($res.source)" }
        if ($sw.ElapsedMilliseconds -ge 2500) { throw "Soft timeout did not answer early ($($sw.ElapsedMilliseconds) ms)." }

        Start-Sleep -Milliseconds 3500
        $sw.Restart()
        $later = Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/$productId" -Method Get
        $sw.Stop()
        if ($later.source -notlike "*HIT*") { throw "Expected a HIT after the background refresh, got: $($later.source)" }
        if ($sw.ElapsedMilliseconds -ge 500) { throw "Expected a fast cached hit, took $($sw.ElapsedMilliseconds) ms." }
    } finally {
        Set-DbLatency $Node1Url 0
    }
}

Write-Host "`nAll E2E scenarios passed with flying colors!" -ForegroundColor Green
