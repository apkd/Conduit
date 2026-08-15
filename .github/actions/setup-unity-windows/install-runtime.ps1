$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$runtimeFiles = @(
    "$env:WINDIR\System32\msvcp100.dll",
    "$env:WINDIR\System32\msvcr100.dll"
)
if (($runtimeFiles | Where-Object { -not (Test-Path $_ -PathType Leaf) }).Count -eq 0) {
    Write-Host "Visual C++ 2010 runtime is already installed."
    return
}

$installer = Join-Path $env:UNITY_ROOT ".conduit-prerequisites\vcredist_x64.exe"
if (-not (Test-Path $installer -PathType Leaf)) {
    throw "The embedded Visual C++ 2010 redistributable is missing."
}

$process = Start-Process $installer -Wait -PassThru -ArgumentList "/q", "/norestart"
if ($process.ExitCode -notin 0, 1638, 3010) {
    throw "Visual C++ 2010 redistributable exited with code $($process.ExitCode)."
}

foreach ($runtimeFile in $runtimeFiles) {
    if (-not (Test-Path $runtimeFile -PathType Leaf)) {
        throw "Visual C++ 2010 runtime file is missing after installation: $runtimeFile"
    }
}
