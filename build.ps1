param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProgressionProject = Join-Path $Root "src/GuidedArrow.Progression/GuidedArrow.Progression.csproj"
$Artifacts = Join-Path $Root "artifacts"
$ProgressionBuildOut = Join-Path $Artifacts "progression-build"
$Stage = Join-Path $Artifacts "stage"
$Dist = Join-Path $Root "dist"
$Checksums = Join-Path $Root "checksums/SHA256SUMS.txt"
$Version = "1.3.0"
$ExpectedCoreSha256 = "0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0"

Remove-Item $Artifacts -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $Dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $ProgressionBuildOut, $Stage, $Dist | Out-Null

$ModuleBin = Join-Path $Root "module/GuidedArrow/bin/Win64_Shipping_Client"
$CoreDll = Join-Path $ModuleBin "GuidedArrow.dll"
if (-not (Test-Path $CoreDll)) {
    throw "The preserved GuidedArrow.dll core runtime is missing."
}
$coreHash = (Get-FileHash $CoreDll -Algorithm SHA256).Hash.ToLowerInvariant()
if ($coreHash -ne $ExpectedCoreSha256) {
    throw "GuidedArrow.dll integrity failure. Expected stable v1.1.17 core $ExpectedCoreSha256, got $coreHash. Only the verified core may be packaged."
}
Write-Host "Verified stable GuidedArrow.dll SHA-256: $coreHash"

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
    $CoreDll,
    (Join-Path $ModuleBin "GuidedArrow.Progression.dll"),
    (Join-Path $ModuleBin "GuidedArrow.Progression.pdb")
)
$Lines = foreach ($File in $Files) {
    $Hash = (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant()
    "$Hash  $([IO.Path]::GetFileName($File))"
}
$Lines | Set-Content $Checksums -Encoding UTF8
$Lines | ForEach-Object { Write-Host $_ }
