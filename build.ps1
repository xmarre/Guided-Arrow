param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$CoreProject = Join-Path $Root "src/GuidedArrow.Core/GuidedArrow.Core.csproj"
$ProgressionProject = Join-Path $Root "src/GuidedArrow.Progression/GuidedArrow.Progression.csproj"
$Artifacts = Join-Path $Root "artifacts"
$CoreBuildOut = Join-Path $Artifacts "core-build"
$ProgressionBuildOut = Join-Path $Artifacts "progression-build"
$Stage = Join-Path $Artifacts "stage"
$Dist = Join-Path $Root "dist"
$Checksums = Join-Path $Root "checksums/SHA256SUMS.txt"
$Version = "1.2.2"

Remove-Item $Artifacts -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $Dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $CoreBuildOut, $ProgressionBuildOut, $Stage, $Dist | Out-Null

$ModuleBin = Join-Path $Root "module/GuidedArrow/bin/Win64_Shipping_Client"
New-Item -ItemType Directory -Force -Path $ModuleBin | Out-Null

if (Test-Path $CoreProject) {
    dotnet restore $CoreProject
    dotnet build $CoreProject -c $Configuration --no-restore -o $CoreBuildOut /p:ContinuousIntegrationBuild=true
    Copy-Item (Join-Path $CoreBuildOut "GuidedArrow.dll") (Join-Path $ModuleBin "GuidedArrow.dll") -Force
    Copy-Item (Join-Path $CoreBuildOut "GuidedArrow.pdb") (Join-Path $ModuleBin "GuidedArrow.pdb") -Force
}
elseif (-not (Test-Path (Join-Path $ModuleBin "GuidedArrow.dll"))) {
    throw "Neither the Guided Arrow core project nor the preserved core DLL is available."
}

dotnet restore $ProgressionProject
dotnet build $ProgressionProject -c $Configuration --no-restore -o $ProgressionBuildOut /p:ContinuousIntegrationBuild=true
Copy-Item (Join-Path $ProgressionBuildOut "GuidedArrow.Progression.dll") (Join-Path $ModuleBin "GuidedArrow.Progression.dll") -Force
Copy-Item (Join-Path $ProgressionBuildOut "GuidedArrow.Progression.pdb") (Join-Path $ModuleBin "GuidedArrow.Progression.pdb") -Force

$ModuleStage = Join-Path $Stage "GuidedArrow"
Copy-Item (Join-Path $Root "module/GuidedArrow") $ModuleStage -Recurse -Force

$CompiledZip = Join-Path $Dist "GuidedArrow-v$Version-Bannerlord-1.3.15-to-1.4.7-Universal.zip"
Compress-Archive -Path $ModuleStage -DestinationPath $CompiledZip -CompressionLevel Optimal

$SourceStage = Join-Path $Stage "GuidedArrow-v$Version-SOURCE"
New-Item -ItemType Directory -Force -Path $SourceStage | Out-Null
Copy-Item (Join-Path $Root "src") $SourceStage -Recurse -Force
Copy-Item (Join-Path $Root "module") $SourceStage -Recurse -Force
Copy-Item (Join-Path $Root "tools") $SourceStage -Recurse -Force
Copy-Item (Join-Path $Root "README.md"), (Join-Path $Root "CHANGELOG.md"), (Join-Path $Root "BUILD.md"), (Join-Path $Root "GuidedArrow.sln"), (Join-Path $Root "build.ps1") $SourceStage -Force

Get-ChildItem $SourceStage -Directory -Recurse -Force |
    Where-Object { $_.Name -in @('bin', 'obj', '__pycache__') } |
    Sort-Object FullName -Descending |
    Remove-Item -Recurse -Force
Get-ChildItem $SourceStage -File -Recurse -Force |
    Where-Object { $_.Extension -in @('.user', '.suo', '.nupkg', '.pyc') } |
    Remove-Item -Force

$SourceZip = Join-Path $Dist "GuidedArrow-v$Version-SOURCE.zip"
Compress-Archive -Path (Join-Path $SourceStage "*") -DestinationPath $SourceZip -CompressionLevel Optimal

$Files = @(
    $CompiledZip,
    $SourceZip,
    (Join-Path $ModuleStage "bin/Win64_Shipping_Client/GuidedArrow.dll"),
    (Join-Path $ModuleStage "bin/Win64_Shipping_Client/GuidedArrow.pdb"),
    (Join-Path $ModuleStage "bin/Win64_Shipping_Client/GuidedArrow.Progression.dll"),
    (Join-Path $ModuleStage "bin/Win64_Shipping_Client/GuidedArrow.Progression.pdb")
)
$Lines = foreach ($File in $Files) {
    $Hash = (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant()
    "$Hash  $([IO.Path]::GetFileName($File))"
}
$Lines | Set-Content $Checksums -Encoding UTF8
$Lines | ForEach-Object { Write-Host $_ }
