$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$runtimeNames = "msvcp100.dll", "msvcr100.dll"
$editorRoot = Join-Path $env:UNITY_ROOT "Editor"
$systemRuntimeFiles = $runtimeNames | ForEach-Object { Join-Path "$env:WINDIR\System32" $_ }
$installer = Join-Path $env:UNITY_ROOT ".conduit-prerequisites\vcredist_x64.exe"

# direct extraction bypasses Unity's prerequisite setup, so every cache creator installs it once.
$process = Start-Process $installer -Wait -PassThru -ArgumentList "/q", "/norestart"
if ($process.ExitCode -notin 0, 1638, 3010) {
    throw "Visual C++ 2010 redistributable exited with code $($process.ExitCode)."
}

Copy-Item -Path $systemRuntimeFiles -Destination $editorRoot
