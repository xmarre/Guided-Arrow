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
$StableStage = Join-Path $Artifacts "stage-stable"
$CandidateStage = Join-Path $Artifacts "stage-source-core-candidate"
$SourceStageRoot = Join-Path $Artifacts "stage-source"
$Dist = Join-Path $Root "dist"
$Checksums = Join-Path $Root "checksums/SHA256SUMS.txt"
$Version = "1.3.6"
$ExpectedCoreSha256 = "0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0"
$StableZipTimestamp = [DateTimeOffset]::Parse("2000-01-01T00:00:00Z")

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-DeterministicZip {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $sourceRoot = (Resolve-Path $SourceDirectory).Path.TrimEnd([char[]]@('\', '/'))
    Remove-Item $DestinationPath -Force -ErrorAction SilentlyContinue

    $fileStream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $fileStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            $files = Get-ChildItem $sourceRoot -File -Recurse -Force | Sort-Object FullName
            foreach ($file in $files) {
                $relativePath = $file.FullName.Substring($sourceRoot.Length).TrimStart([char[]]@('\', '/'))
                $entryName = $relativePath.Replace('\', '/')
                $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $StableZipTimestamp

                $inputStream = [System.IO.File]::OpenRead($file.FullName)
                $outputStream = $entry.Open()
                try {
                    $inputStream.CopyTo($outputStream)
                }
                finally {
                    $outputStream.Dispose()
                    $inputStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $fileStream.Dispose()
    }
}

function New-ModuleStage {
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$CoreDll,
        [Parameter(Mandatory = $true)][string]$ProgressionDll,
        [Parameter(Mandatory = $true)][string]$ProgressionPdb
    )

    $moduleSource = Join-Path $Root "module/GuidedArrow"
    Copy-Item $moduleSource $Destination -Recurse -Force
    $destinationBin = Join-Path $Destination "bin/Win64_Shipping_Client"
    Copy-Item $CoreDll (Join-Path $destinationBin "GuidedArrow.dll") -Force
    Copy-Item $ProgressionDll (Join-Path $destinationBin "GuidedArrow.Progression.dll") -Force
    Copy-Item $ProgressionPdb (Join-Path $destinationBin "GuidedArrow.Progression.pdb") -Force
}

Remove-Item $Artifacts -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $Dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $CoreBuildOut, $ProgressionBuildOut, $StableStage, $CandidateStage, $SourceStageRoot, $Dist | Out-Null

$ModuleBin = Join-Path $Root "module/GuidedArrow/bin/Win64_Shipping_Client"
$VerifiedCoreDll = Join-Path $ModuleBin "GuidedArrow.dll"
if (-not (Test-Path $VerifiedCoreDll)) {
    throw "The verified GuidedArrow.dll core runtime is missing."
}
$verifiedCoreHash = (Get-FileHash $VerifiedCoreDll -Algorithm SHA256).Hash.ToLowerInvariant()
if ($verifiedCoreHash -ne $ExpectedCoreSha256) {
    throw "GuidedArrow.dll integrity failure. Expected stable v1.1.17 core $ExpectedCoreSha256, got $verifiedCoreHash."
}
Write-Host "Verified binary core SHA-256: $verifiedCoreHash"

dotnet restore $CoreProject
dotnet build $CoreProject -c $Configuration --no-restore -o $CoreBuildOut /p:ContinuousIntegrationBuild=true /p:UseSharedCompilation=false
$SourceCoreDll = Join-Path $CoreBuildOut "GuidedArrow.dll"
if (-not (Test-Path $SourceCoreDll)) {
    throw "The source-built GuidedArrow.dll was not produced."
}
$sourceCoreHash = (Get-FileHash $SourceCoreDll -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Source-built core SHA-256: $sourceCoreHash"

dotnet restore $ProgressionProject
dotnet build $ProgressionProject -c $Configuration --no-restore -o $ProgressionBuildOut /p:ContinuousIntegrationBuild=true /p:UseSharedCompilation=false
$ProgressionDll = Join-Path $ProgressionBuildOut "GuidedArrow.Progression.dll"
$ProgressionPdb = Join-Path $ProgressionBuildOut "GuidedArrow.Progression.pdb"

$StableModuleStage = Join-Path $StableStage "GuidedArrow"
$CandidateModuleStage = Join-Path $CandidateStage "GuidedArrow"
New-ModuleStage -Destination $StableModuleStage -CoreDll $VerifiedCoreDll -ProgressionDll $ProgressionDll -ProgressionPdb $ProgressionPdb
New-ModuleStage -Destination $CandidateModuleStage -CoreDll $SourceCoreDll -ProgressionDll $ProgressionDll -ProgressionPdb $ProgressionPdb

$CompiledZip = Join-Path $Dist "GuidedArrow-v$Version-Bannerlord-1.3.15-to-1.4.7-Universal.zip"
$CandidateZip = Join-Path $Dist "GuidedArrow-v$Version-SOURCE-CORE-CANDIDATE-Bannerlord-1.3.15-to-1.4.7-Universal.zip"
New-DeterministicZip -SourceDirectory $StableStage -DestinationPath $CompiledZip
New-DeterministicZip -SourceDirectory $CandidateStage -DestinationPath $CandidateZip

$SourceStage = Join-Path $SourceStageRoot "GuidedArrow-v$Version-SOURCE"
New-Item -ItemType Directory -Force -Path $SourceStage | Out-Null
Copy-Item (Join-Path $Root "src") $SourceStage -Recurse -Force
Copy-Item (Join-Path $Root "module") $SourceStage -Recurse -Force
Copy-Item (Join-Path $Root "README.md"), (Join-Path $Root "CONFIGURATION.md"), (Join-Path $Root "CHANGELOG.md"), (Join-Path $Root "BUILD.md"), (Join-Path $Root "CORE_SOURCE_MIGRATION.md"), (Join-Path $Root "GuidedArrow.sln"), (Join-Path $Root "build.ps1") $SourceStage -Force

Get-ChildItem $SourceStage -Directory -Recurse -Force |
    Where-Object { $_.Name -in @('bin', 'obj', '__pycache__') } |
    Sort-Object FullName -Descending |
    Remove-Item -Recurse -Force
Get-ChildItem $SourceStage -File -Recurse -Force |
    Where-Object { $_.Extension -in @('.user', '.suo', '.nupkg', '.pyc') } |
    Remove-Item -Force

$SourceZip = Join-Path $Dist "GuidedArrow-v$Version-SOURCE.zip"
New-DeterministicZip -SourceDirectory $SourceStageRoot -DestinationPath $SourceZip

$Files = @(
    $CompiledZip,
    $CandidateZip,
    $SourceZip,
    $VerifiedCoreDll,
    $SourceCoreDll,
    $ProgressionDll,
    $ProgressionPdb
)
$Lines = foreach ($File in $Files) {
    $Hash = (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant()
    "$Hash  $([IO.Path]::GetFileName($File))"
}
$Lines | Set-Content $Checksums -Encoding UTF8
$Lines | ForEach-Object { Write-Host $_ }
