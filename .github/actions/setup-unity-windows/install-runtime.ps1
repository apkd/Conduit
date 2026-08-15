$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$installer = Join-Path $env:RUNNER_TEMP "vc_redist.x64.exe"
$fileName = Split-Path -Leaf $installer
& aria2c.exe `
    --allow-overwrite=true `
    --auto-file-renaming=false `
    --console-log-level=warn `
    --dir=$env:RUNNER_TEMP `
    --file-allocation=none `
    --max-connection-per-server=4 `
    --min-split-size=4M `
    --out=$fileName `
    --split=4 `
    --summary-interval=0 `
    "https://aka.ms/vs/17/release/vc_redist.x64.exe"
if ($LASTEXITCODE -ne 0) {
    throw "Visual C++ runtime download failed."
}

$process = Start-Process $installer -Wait -PassThru -ArgumentList "/install", "/quiet", "/norestart"
if ($process.ExitCode -notin 0, 3010) {
    throw "Visual C++ runtime installer exited with code $($process.ExitCode)."
}
