$env:Path = "C:\Program Files\dotnet;$env:Path"
Push-Location (Join-Path $PSScriptRoot "..")
dotnet run --project OpenBurningSuite/OpenBurningSuite.csproj
Pop-Location
