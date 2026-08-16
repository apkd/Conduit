$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$runtimeNames = "msvcp100.dll", "msvcr100.dll"
$editorRoot = Join-Path $env:UNITY_ROOT "Editor"
$cachedRuntimeFiles = $runtimeNames | ForEach-Object { Join-Path $editorRoot $_ }
if (@($cachedRuntimeFiles | Where-Object { -not (Test-Path $_ -PathType Leaf) }).Count -eq 0) {
    # Unity.dll resolves these beside Unity.exe; PATH also covers helper processes launched by Unity.
    $editorRoot | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append
    Write-Host "Using the cached app-local Visual C++ 2010 runtime."
    return
}

$portableRuntimeFiles = $runtimeNames | ForEach-Object { Join-Path $env:UNITY_RUNTIME_CACHE $_ }
if (@($portableRuntimeFiles | Where-Object { -not (Test-Path $_ -PathType Leaf) }).Count -eq 0) {
    Copy-Item $portableRuntimeFiles $editorRoot
    $editorRoot | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append
    Write-Host "Using the cached portable Visual C++ 2010 runtime."
    return
}

$systemRuntimeFiles = $runtimeNames | ForEach-Object { Join-Path "$env:WINDIR\System32" $_ }
$installer = Join-Path $env:UNITY_ROOT ".conduit-prerequisites\vcredist_x64.exe"
if (-not (Test-Path $installer -PathType Leaf)) {
    throw "The embedded Visual C++ 2010 redistributable is missing."
}

if (@($systemRuntimeFiles | Where-Object { -not (Test-Path $_ -PathType Leaf) }).Count -ne 0) {
    $process = Start-Process $installer -Wait -PassThru -ArgumentList "/q", "/norestart"
    if ($process.ExitCode -notin 0, 1638, 3010) {
        throw "Visual C++ 2010 redistributable exited with code $($process.ExitCode)."
    }
}

New-Item -ItemType Directory -Force $env:UNITY_RUNTIME_CACHE | Out-Null
foreach ($runtimeFile in $systemRuntimeFiles) {
    if (-not (Test-Path $runtimeFile -PathType Leaf)) {
        throw "Visual C++ 2010 runtime file is missing after installation: $runtimeFile"
    }

    Copy-Item $runtimeFile $editorRoot
    Copy-Item $runtimeFile $env:UNITY_RUNTIME_CACHE
}

$editorRoot | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append
Write-Host "Cached the app-local Visual C++ 2010 runtime."
