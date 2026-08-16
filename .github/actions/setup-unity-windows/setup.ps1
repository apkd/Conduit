$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-SegmentedDownload {
    param(
        [Parameter(Mandatory)] [string] $Uri,
        [Parameter(Mandatory)] [string] $Output,
        [int] $Connections = 16
    )

    $directory = Split-Path -Parent $Output
    $fileName = Split-Path -Leaf $Output
    New-Item -ItemType Directory -Force $directory | Out-Null

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        & aria2c.exe `
            --allow-overwrite=true `
            --auto-file-renaming=false `
            --console-log-level=warn `
            --continue=true `
            --connect-timeout=30 `
            --dir=$directory `
            --file-allocation=none `
            --max-connection-per-server=$Connections `
            --max-tries=1 `
            --min-split-size=4M `
            --out=$fileName `
            --retry-wait=0 `
            --split=$Connections `
            --summary-interval=0 `
            $Uri
        if ($LASTEXITCODE -eq 0) {
            return
        }

        # aria2 can resume only while the partial file retains its control file.
        if ((Test-Path $Output) -and -not (Test-Path "$Output.aria2")) {
            Remove-Item -Force $Output
        }
        if ($attempt -eq 5) {
            throw "Download failed after five attempts: $Uri"
        }

        Start-Sleep -Seconds (2 -shl ($attempt - 1))
    }
}

$work = Join-Path $env:RUNNER_TEMP "unity-windows-editor-$env:UNITY_VERSION"
$releasePage = Join-Path $work "whats-new.html"
$archivePage = Join-Path $work "archive.html"
$installer = $env:UNITY_INSTALLER
$extractorRoot = Join-Path $PSScriptRoot "nsisbi-extract"
$extractorManifest = Join-Path $extractorRoot "Cargo.toml"
$builtExtractor = Join-Path $extractorRoot "target\release\unity-nsisbi-extract.exe"
$extractor = $env:UNITY_EXTRACTOR

Remove-Item -Recurse -Force $work, $env:UNITY_ROOT -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work | Out-Null

$extractorBuild = $null
if (-not (Test-Path $extractor -PathType Leaf)) {
    # Compile concurrently with the much larger Editor download so this cold-only cost is hidden.
    $escapedManifest = $extractorManifest.Replace('"', '\"')
    $extractorBuild = Start-Process cargo.exe -NoNewWindow -PassThru -ArgumentList `
        "build --release --locked --manifest-path `"$escapedManifest`""
}

if (-not (Test-Path $installer -PathType Leaf)) {
    Invoke-SegmentedDownload `
        -Uri "https://unity.com/releases/editor/whats-new/$env:UNITY_VERSION" `
        -Output $releasePage `
        -Connections 1

    $escapedVersion = [regex]::Escape($env:UNITY_VERSION)
    $downloadPattern = "https?://(?:download\.unity3d\.com/download_unity|beta\.unity3d\.com/download)/[0-9a-f]+/Windows64EditorInstaller/UnitySetup64-$escapedVersion\.exe"
    $downloadMatch = [regex]::Match((Get-Content -Raw $releasePage), $downloadPattern)
    if ($downloadMatch.Success) {
        $editorUrl = $downloadMatch.Value
    } else {
        Invoke-SegmentedDownload `
            -Uri "https://unity.com/releases/editor/archive" `
            -Output $archivePage `
            -Connections 1
        $revisionMatch = [regex]::Match(
            (Get-Content -Raw $archivePage),
            "unityhub://$escapedVersion/(?<revision>[0-9a-f]+)"
        )
        if (-not $revisionMatch.Success) {
            throw "Could not resolve the Unity revision for $env:UNITY_VERSION."
        }

        $revision = $revisionMatch.Groups["revision"].Value
        $editorUrl = "https://download.unity3d.com/download_unity/$revision/Windows64EditorInstaller/UnitySetup64-$env:UNITY_VERSION.exe"
    }

    Write-Host "Downloading $editorUrl"
    $downloadTimer = [Diagnostics.Stopwatch]::StartNew()
    Invoke-SegmentedDownload -Uri $editorUrl -Output $installer
    $downloadTimer.Stop()
    Write-Host ("Editor download completed in {0:N1}s" -f $downloadTimer.Elapsed.TotalSeconds)
} else {
    Write-Host "Using the cached Unity Editor installer."
}

if ($null -ne $extractorBuild) {
    $extractorBuild.WaitForExit()
    if ($extractorBuild.ExitCode -ne 0) {
        throw "NSISBI extractor build exited with code $($extractorBuild.ExitCode)."
    }

    Copy-Item $builtExtractor $extractor
}

$unusedPaths = @(
    "Editor\BugReporter",
    "Editor\Data\MonoEmbedRuntime",
    "Editor\Data\Resources\GI",
    "Editor\Data\Resources\OpenRL",
    "Editor\Data\Resources\PackageManager\Diagnostics",
    "Editor\Data\Resources\PackageManager\PackageTemplates",
    "Editor\Data\Resources\PackageManager\ProjectTemplates",
    "Editor\Data\Resources\PackageManager\BuiltInPackages\com.unity.2d.sprite",
    "Editor\Data\Resources\PackageManager\BuiltInPackages\com.unity.2d.tilemap",
    "Editor\Data\Resources\PackageManager\BuiltInPackages\com.unity.multiplayer.center",
    "Editor\Data\Resources\PackageManager\BuiltInPackages\com.unity.path-tracing",
    "Editor\Data\Resources\PackageManager\BuiltInPackages\com.unity.render-pipelines.high-definition",
    "Editor\Data\Resources\PackageManager\BuiltInPackages\com.unity.render-pipelines.high-definition-config",
    "Editor\Data\Resources\PackageManager\BuiltInPackages\com.unity.rendering.denoising",
    "Editor\Data\Resources\PackageManager\BuiltInPackages\com.unity.shaderanalysis",
    "Editor\Data\Resources\PackageManager\BuiltInPackages\com.unity.visualeffectgraph",
    "Editor\Data\Tools\LightBaker",
    "Editor\Data\Tools\PVRTexTool",
    "Editor\Data\Tools\VersionControl",
    "Editor\Data\Tools\macosx",
    "Editor\Data\Tools\usymtool"
)
$extractorArguments = @($installer, $env:UNITY_ROOT)
$extractorArguments += "--exclude-prefix", '$PLUGINSDIR'
$extractorArguments += "--force-include", '$PLUGINSDIR\vcredist_x64.exe'
foreach ($relativePath in $unusedPaths) {
    $extractorArguments += "--exclude-prefix", $relativePath
}
foreach ($suffix in ".a", ".dbg", ".la", ".mdb", ".pdb", ".tgz", "_s.debug") {
    $extractorArguments += "--exclude-suffix", $suffix
}

$extractTimer = [Diagnostics.Stopwatch]::StartNew()
& $extractor @extractorArguments
if ($LASTEXITCODE -ne 0) {
    throw "NSISBI extraction exited with code $LASTEXITCODE."
}
$extractTimer.Stop()
Write-Host ("Editor extraction completed in {0:N1}s" -f $extractTimer.Elapsed.TotalSeconds)

$pluginRoot = Join-Path $env:UNITY_ROOT '$PLUGINSDIR'
$prerequisiteRoot = Join-Path $env:UNITY_ROOT ".conduit-prerequisites"
New-Item -ItemType Directory -Force $prerequisiteRoot | Out-Null
Move-Item (Join-Path $pluginRoot "vcredist_x64.exe") $prerequisiteRoot
Remove-Item -Recurse -Force $pluginRoot

$packageManagerEditor = Join-Path $env:UNITY_ROOT "Editor\Data\Resources\PackageManager\Editor"
if (Test-Path $packageManagerEditor) {
    Get-ChildItem -Path $packageManagerEditor -File |
        Where-Object { $_.Name -ne "manifest.json" } |
        Remove-Item -Force
}

Set-Content -NoNewline -Path "$env:UNITY_ROOT\.conduit-cache-version" -Value $env:UNITY_VERSION
Remove-Item -Recurse -Force $work

$files = Get-ChildItem -Path $env:UNITY_ROOT -File -Recurse
$size = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ("Stripped Unity Editor: {0:N0} files, {1:N2} GiB" -f $files.Count, ($size / 1GB))
