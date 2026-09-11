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
    $res1 = Invoke-RestMethod -Uri "$Node1Url/api/products/tenant_alpha/102" -Method Get
    $queries1 = $res1.totalDbQueriesAcrossAllCalls

    $res2 = Invoke-RestMethod -Uri "$Node2Url/api/products/tenant_alpha/102" -Method Get
    if ($res2.source -notlike "*HIT*") { throw "Expected L2 HIT on Node 2, got: $($res2.source)" }
    $queries2 = $res2.totalDbQueriesAcrossAllCalls

    if ($queries1 -ne $queries2) { throw "Node 2 triggered a DB query instead of reading from Redis L2!" }
}

# Scenario 3: Stampede Prevention
Invoke-TestStep "Scenario 3: Stampede concurrency protection (100 concurrent requests, exactly 1 DB query)" {
    Invoke-RestMethod -Uri "$Node1Url/api/system/reset-db-counter" -Method Post | Out-Null

    $productId = 99999
    $tasks = 1..100 | ForEach-Object {
        [System.Threading.Tasks.Task]::Run([Func[object]]{
            (Invoke-WebRequest -Uri "$Node1Url/api/products/tenant_alpha/$productId" -UseBasicParsing).StatusCode
        })
    }
    [System.Threading.Tasks.Task]::WaitAll($tasks)

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

Write-Host "`nAll E2E scenarios passed with flying colors!" -ForegroundColor Green
