param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root "src/GuidedArrow.Progression/GuidedArrow.Progression.csproj"
$Artifacts = Join-Path $Root "artifacts"
$BuildOut = Join-Path $Artifacts "build"
$Stage = Join-Path $Artifacts "stage"
$Dist = Join-Path $Root "dist"
$Checksums = Join-Path $Root "checksums/SHA256SUMS.txt"
$Version = "1.2.1"

Remove-Item $Artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $BuildOut, $Stage, $Dist | Out-Null

dotnet restore $Project
dotnet build $Project -c $Configuration --no-restore -o $BuildOut /p:ContinuousIntegrationBuild=true

$ModuleBin = Join-Path $Root "module/GuidedArrow/bin/Win64_Shipping_Client"
New-Item -ItemType Directory -Force -Path $ModuleBin | Out-Null
Copy-Item (Join-Path $BuildOut "GuidedArrow.Progression.dll") (Join-Path $ModuleBin "GuidedArrow.Progression.dll") -Force
Copy-Item (Join-Path $BuildOut "GuidedArrow.Progression.pdb") (Join-Path $ModuleBin "GuidedArrow.Progression.pdb") -Force

$ModuleStage = Join-Path $Stage "GuidedArrow"
Copy-Item (Join-Path $Root "module/GuidedArrow") $ModuleStage -Recurse -Force

$CompiledZip = Join-Path $Dist "GuidedArrow-v$Version-Bannerlord-1.3.15-to-1.4.7-Universal.zip"
Remove-Item $CompiledZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $ModuleStage -DestinationPath $CompiledZip -CompressionLevel Optimal

$SourceStage = Join-Path $Stage "GuidedArrow-v$Version-SOURCE"
New-Item -ItemType Directory -Force -Path $SourceStage | Out-Null
Copy-Item (Join-Path $Root "src") $SourceStage -Recurse -Force
Copy-Item (Join-Path $Root "module") $SourceStage -Recurse -Force
Copy-Item (Join-Path $Root "README.md"), (Join-Path $Root "CHANGELOG.md"), (Join-Path $Root "BUILD.md"), (Join-Path $Root "GuidedArrow.sln"), (Join-Path $Root "build.ps1") $SourceStage -Force
$SourceZip = Join-Path $Dist "GuidedArrow-v$Version-SOURCE.zip"
Remove-Item $SourceZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $SourceStage "*") -DestinationPath $SourceZip -CompressionLevel Optimal

$Files = @(
    $CompiledZip,
    $SourceZip,
    (Join-Path $ModuleStage "bin/Win64_Shipping_Client/GuidedArrow.dll"),
    (Join-Path $ModuleStage "bin/Win64_Shipping_Client/GuidedArrow.Progression.dll"),
    (Join-Path $ModuleStage "bin/Win64_Shipping_Client/GuidedArrow.Progression.pdb")
)
$Lines = foreach ($File in $Files) {
    $Hash = (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant()
    "$Hash  $([IO.Path]::GetFileName($File))"
}
$Lines | Set-Content $Checksums -Encoding UTF8
$Lines | ForEach-Object { Write-Host $_ }
