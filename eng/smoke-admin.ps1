#requires -Version 7.0
[CmdletBinding()]
param([int] $Port = 5391, [switch] $KeepRunning, [string] $Artifacts = '.artifacts/release/admin')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not [IO.Path]::IsPathRooted($Artifacts)) { $Artifacts = Join-Path $repo $Artifacts }
$Artifacts = [IO.Path]::GetFullPath($Artifacts)
New-Item -ItemType Directory -Force $Artifacts | Out-Null
$name = 'co-smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
$origin = "$name-origin"
$admin = "$name-admin"
$proxy = "$name-proxy"
$containers = [Collections.Generic.List[string]]::new()
$succeeded = $false
function Invoke-SmokeDocker([string[]] $Arguments) {
    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: $output" }
    return $output
}
function Request([string] $Path, [switch] $Authenticated) {
    $headers = @{}
    if ($Authenticated) { $headers.Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('operator:smoke-only')) }
    Invoke-WebRequest "http://127.0.0.1:$Port$Path" -Headers $headers -SkipHttpErrorCheck -TimeoutSec 10
}
try {
    Invoke-SmokeDocker @('network', 'create', $name) | Out-Null
    $sample = Join-Path $repo 'samples/CacheOrchestrator.Minimal/bin/Release/net10.0'
    $containers.Add($origin)
    Invoke-SmokeDocker @('run', '-d', '--name', $origin, '--network', $name, '--mount', "type=bind,source=$sample,target=/app,readonly", '-w', '/app',
        'mcr.microsoft.com/dotnet/aspnet:10.0', 'dotnet', 'CacheOrchestrator.Minimal.dll', '--urls', 'http://+:8080') | Out-Null
    @{
        AdminConsole = @{
            ApiKey = 'dev-admin-key'; RequestTimeoutMs = 1000; DownReprobeSeconds = 5
            Instances = @(@{ id = 'healthy'; url = "http://${origin}:8080" }, @{ id = 'offline'; url = 'http://127.0.0.1:1' })
            Metrics = @{ Enabled = $false }
        }
    } | ConvertTo-Json -Depth 6 | Set-Content "$Artifacts/appsettings.smoke.json"
    $containers.Add($admin)
    Invoke-SmokeDocker @('run', '-d', '--name', $admin, '--network', $name, '--mount', "type=bind,source=$Artifacts/appsettings.smoke.json,target=/app/appsettings.Production.json,readonly",
        'cacheorchestrator-admin-console:verify') | Out-Null
    # Disposable credentials for a real Basic-auth reverse proxy. No product API key
    # reaches the browser; all paths, including static assets/OpenAPI, are protected.
    $hash = [Convert]::ToBase64String([Security.Cryptography.SHA1]::HashData([Text.Encoding]::UTF8.GetBytes('smoke-only')))
    "operator:{SHA}$hash" | Set-Content "$Artifacts/htpasswd"
    @"
events {}
http {
  server {
    listen 8080;
    auth_basic "Admin smoke";
    auth_basic_user_file /etc/nginx/htpasswd;
    location / { proxy_pass http://${admin}:8080; }
  }
}
"@ | Set-Content "$Artifacts/nginx.conf"
    $containers.Add($proxy)
    Invoke-SmokeDocker @('run', '-d', '--name', $proxy, '--network', $name, '-p', "127.0.0.1:${Port}:8080",
        '--mount', "type=bind,source=$Artifacts/nginx.conf,target=/etc/nginx/nginx.conf,readonly",
        '--mount', "type=bind,source=$Artifacts/htpasswd,target=/etc/nginx/htpasswd,readonly", 'nginx:1.28-alpine') | Out-Null
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try { if ((Request '/health' -Authenticated).StatusCode -eq 200) { $ready = $true; break } } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Admin image did not become ready.' }
    foreach ($path in '/', '/api/instances', '/openapi/v1.json') {
        if ((Request $path).StatusCode -ne 401) { throw "Proxy failed to protect $path" }
        if ((Request $path -Authenticated).StatusCode -ne 200) { throw "Authenticated request failed: $path" }
    }
    $instances = (Request '/api/instances' -Authenticated).Content
    $instances | Set-Content "$Artifacts/instances.json"
    if ($instances -notmatch 'Healthy' -or $instances -notmatch 'Down') { throw "Expected healthy and unavailable origins: $instances" }
    $catalog = (Request '/api/domain-settings/catalog' -Authenticated).Content
    if ($catalog -notmatch 'dataCache.ttlSeconds' -or $catalog -match 'dev-admin-key|MaxItemBytes') { throw 'Catalog unavailable or exposes removed/sensitive data.' }
    if ((Request '/' -Authenticated).Content -match 'dev-admin-key') { throw 'Upstream API key exposed in page.' }
    if ($KeepRunning) {
        @{ containers = $containers.ToArray(); network = $name; port = $Port } | ConvertTo-Json | Set-Content "$Artifacts/running.json"
        Write-Output "Admin browser fixture ready at http://127.0.0.1:$Port (operator / smoke-only). Cleanup ownership: $Artifacts/running.json"
    }
    else {
        Invoke-SmokeDocker @('stop', $origin) | Out-Null
        $offline = (Request '/api/domain-settings/catalog' -Authenticated).Content | ConvertFrom-Json
        if (@($offline.settings).Count -ne 0) { throw 'All-down catalog must be empty.' }
        Write-Output 'Admin image smoke passed: proxy authentication, real origin, partial outage, all-down catalog and no API-key exposure.'
    }
    $succeeded = $true
}
finally {
    foreach ($container in $containers) { & docker logs $container *> "$Artifacts/$container.log" }
    if (-not $KeepRunning -or -not $succeeded) {
        foreach ($container in $containers) { & docker rm -f $container | Out-Null }
        & docker network rm $name | Out-Null
    }
}
