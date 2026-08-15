# Pack FrameworkSDK -> NuGet local feed cho các module repo độc lập.
# Các module repo dùng PackageReference "FrameworkSDK" (xem nuget.config của từng module).
# Chạy lại khi FrameworkSDK thay đổi -> bump version nếu cần (mặc định 1.0.0).
# Cách dùng:  powershell -NoProfile -ExecutionPolicy Bypass -File src/scripts/publish_local_feed.ps1

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # scripts -> src -> <repo-root>
$feed = "C:\Users\BDTG\mf-local-feed"
New-Item -ItemType Directory -Force -Path $feed | Out-Null

dotnet pack (Join-Path $repoRoot "src\FrameworkSDK\FrameworkSDK.csproj") -c Release -o $feed
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed" }

Write-Host ""
Write-Host "Feed: $feed"
Get-ChildItem $feed -Filter *.nupkg | ForEach-Object { Write-Host "  $($_.Name)" }
