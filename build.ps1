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
$Version = "1.3.4"
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
dotnet build $ProgressionProject -c $Configuration --no-restore -o $ProgressionBuildOut /p:ContinuousIntegrationBuild=true /p:UseSharedCompilation=false
Copy-Item (Join-Path $ProgressionBuildOut "GuidedArrow.Progression.dll") (Join-Path $ModuleBin "GuidedArrow.Progression.dll") -Force
Copy-Item (Join-Path $ProgressionBuildOut "GuidedArrow.Progression.pdb") (Join-Path $ModuleBin "GuidedArrow.Progression.pdb") -Force

$ModuleStage = Join-Path $Stage "GuidedArrow"
Copy-Item (Join-Path $Root "module/GuidedArrow") $ModuleStage -Recurse -Force

$CompiledZip = Join-Path $Dist "GuidedArrow-v$Version-Bannerlord-1.3.15-to-1.4.7-Universal.zip"
New-DeterministicZip -SourceDirectory $Stage -DestinationPath $CompiledZip

$SourceStage = Join-Path $Stage "GuidedArrow-v$Version-SOURCE"
New-Item -ItemType Directory -Force -Path $SourceStage | Out-Null
Copy-Item (Join-Path $Root "src") $SourceStage -Recurse -Force
Copy-Item (Join-Path $Root "module") $SourceStage -Recurse -Force
Copy-Item (Join-Path $Root "README.md"), (Join-Path $Root "CONFIGURATION.md"), (Join-Path $Root "CHANGELOG.md"), (Join-Path $Root "BUILD.md"), (Join-Path $Root "GuidedArrow.sln"), (Join-Path $Root "build.ps1") $SourceStage -Force

Get-ChildItem $SourceStage -Directory -Recurse -Force |
    Where-Object { $_.Name -in @('bin', 'obj', '__pycache__') } |
    Sort-Object FullName -Descending |
    Remove-Item -Recurse -Force
Get-ChildItem $SourceStage -File -Recurse -Force |
    Where-Object { $_.Extension -in @('.user', '.suo', '.nupkg', '.pyc') } |
    Remove-Item -Force

$SourceZip = Join-Path $Dist "GuidedArrow-v$Version-SOURCE.zip"
New-DeterministicZip -SourceDirectory $SourceStage -DestinationPath $SourceZip

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
