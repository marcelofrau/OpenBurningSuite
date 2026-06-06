param(
    [string]$Arch = "x64"
)

$RID = "win-$Arch"
$OutDir = "$PSScriptRoot\publish"
$ProjectRoot = Join-Path $PSScriptRoot ".."

Write-Host "==> Publishing Open Burning Suite for $RID ..."
dotnet publish -p:PublishReadyToRun=true "$ProjectRoot\OpenBurningSuite\OpenBurningSuite.csproj" `
    -c Release `
    -r $RID `
    --self-contained `
    -o $OutDir

Write-Host "==> Creating ZIP archive: openburningsuite-$RID.zip"
Compress-Archive -Path "$OutDir\*" -DestinationPath "$PSScriptRoot\openburningsuite-$RID.zip" -Force

Write-Host "==> Done! openburningsuite-$RID.zip ready."
