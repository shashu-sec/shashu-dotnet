# SHASHU .NET SDK release gate.
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

function Find-Gitleaks {
    $cmd = Get-Command gitleaks -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($hint in @(
        "$env:TEMP\gitleaks\gitleaks.exe",
        "C:\Users\sathi\AppData\Local\Temp\gitleaks\gitleaks.exe"
    )) {
        if (Test-Path $hint) { return $hint }
    }
    throw "gitleaks not found"
}

Write-Host "== dotnet format (apply then verify) =="
dotnet format Shashu.Sdk.sln
if ($LASTEXITCODE -ne 0) { throw "format" }
dotnet format Shashu.Sdk.sln --verify-no-changes
if ($LASTEXITCODE -ne 0) { throw "format verify" }

Write-Host "== build Release warnings-as-errors (library) =="
dotnet build Shashu.Sdk\Shashu.Sdk.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "build" }

Write-Host "== test Release =="
dotnet test Shashu.Sdk.sln -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "test" }

Write-Host "== pack =="
$nupkgDir = Join-Path $Root "nupkg"
New-Item -ItemType Directory -Force -Path $nupkgDir | Out-Null
dotnet pack Shashu.Sdk\Shashu.Sdk.csproj -c Release --no-build -o $nupkgDir
if ($LASTEXITCODE -ne 0) { throw "pack" }

Write-Host "== nupkg contents =="
$nupkg = Get-ChildItem "$nupkgDir\Shashu.Sdk.1.1.1.nupkg" | Select-Object -First 1
$zip = Join-Path $env:TEMP "shashu-sdk-nupkg.zip"
$tmp = Join-Path $env:TEMP "shashu-nupkg-check"
Copy-Item $nupkg.FullName $zip -Force
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $tmp -Force
$names = Get-ChildItem $tmp -Recurse | ForEach-Object { $_.FullName.Substring($tmp.Length) }
if (-not ($names -match "README.md")) { throw "README missing from nupkg" }
if (-not ($names -match "LICENSE")) { throw "LICENSE missing from nupkg" }
$bad = $names | Where-Object { $_ -match "_internal|IDENTITY-I4|Shashu.Sdk.Tests" }
if ($bad) { $bad | ForEach-Object { Write-Host $_ }; throw "nupkg contains internals" }

Write-Host "== vulnerable packages =="
dotnet list Shashu.Sdk\Shashu.Sdk.csproj package --vulnerable --include-transitive
if ($LASTEXITCODE -ne 0) { throw "vulnerable" }

Write-Host "== gitleaks =="
$gl = Find-Gitleaks
& $gl detect --no-git --source $Root --redact
if ($LASTEXITCODE -ne 0) { throw "gitleaks" }

Write-Host "ALL CHECKS PASSED"
