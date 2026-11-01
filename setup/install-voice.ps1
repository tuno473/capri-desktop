# Installs Capri's voice on Windows: Kokoro-FastAPI, its model, and the voice, answering on this computer only.
# It needs git, and about 3 GB. Run it again at any time; it picks up where it stopped.
#   CAPRI_VOICE_DIR   where to install it (default %LOCALAPPDATA%\Capri\voice-server)
#   CAPRI_VOICE_PORT  the port (default 8880, the one Capri expects)
#   CAPRI_VOICE_TEST  1 starts it, checks it speaks and stops it, without setting it to start at login
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$tag = 'v0.9.2'
$commit = '06902e7ea0f51eed396bd3d253864f44218fec81'
$voice = 'af_cortes_tuned'
$voiceSha = '9a183d7f71a9c5dbea3b83750c7e9cc9e17c13ba80b9f0dca6e7be20514c4100'
$voiceUrl = "https://raw.githubusercontent.com/tuno473/capri-desktop/main/voices/$voice.pt"
$dir = if ($env:CAPRI_VOICE_DIR) { $env:CAPRI_VOICE_DIR } else { Join-Path $env:LOCALAPPDATA 'Capri\voice-server' }
$port = if ($env:CAPRI_VOICE_PORT) { [int]$env:CAPRI_VOICE_PORT } else { 8880 }
$test = $env:CAPRI_VOICE_TEST -eq '1'

function Say($words) { Write-Host "capri voice: $words" }
function Die($words) { Write-Host "capri voice: $words"; exit 1 }
function HasVoice {
    try { return (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 "http://127.0.0.1:$port/v1/audio/voices").Content -match "`"$voice`"" }
    catch { return $false }
}
function PortTaken {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $port); return $true }
    catch { return $false }
    finally { $client.Close() }
}
function Must($what) { if ($LASTEXITCODE -ne 0) { Die "$what didn't work." } }

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Die 'It needs git. Install it from git-scm.com and run this again.' }
if (-not $test -and (HasVoice)) { Say "Capri's voice is already answering on port $port."; exit 0 }
if (PortTaken) { Die "Something else is using port $port. Close it, or set CAPRI_VOICE_PORT and tell Capri the new address." }

$env:Path = (Join-Path $env:USERPROFILE '.local\bin') + ';' + $env:Path
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    Say "Installing uv, which sets up Kokoro's Python..."
    Invoke-RestMethod https://astral.sh/uv/install.ps1 | Invoke-Expression
    $env:Path = (Join-Path $env:USERPROFILE '.local\bin') + ';' + $env:Path
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
$kokoro = Join-Path $dir 'kokoro'
if (-not (Test-Path (Join-Path $kokoro '.git'))) {
    Say "Downloading Kokoro-FastAPI $tag..."
    $partial = "$kokoro.partial"
    if (Test-Path $partial) { Remove-Item -Recurse -Force $partial }
    git -c advice.detachedHead=false clone -q --depth 1 --branch $tag https://github.com/remsky/Kokoro-FastAPI.git $partial
    Must 'Downloading Kokoro-FastAPI'
    if ((git -C $partial rev-parse HEAD) -ne $commit) { Die "Kokoro-FastAPI $tag isn't the version this was tried with. Nothing was installed." }
    Move-Item $partial $kokoro
}
Set-Location $kokoro

Say "Installing Kokoro's Python packages (this is the big download)..."
# Kokoro's own lockfile, so everyone gets the exact versions it was tried with.
uv sync -q --frozen --extra cpu
Must "Installing Kokoro's packages"

Say "Downloading Kokoro's model..."
$env:PYTHONUTF8 = '1'
& .venv\Scripts\python.exe docker\scripts\download_model.py --output api\src\models\v1_0
Must "Downloading Kokoro's model"

$target = "api\src\voices\v1_0\$voice.pt"
$here = if ($PSScriptRoot) { Join-Path $PSScriptRoot "..\voices\$voice.pt" } else { $null }
if ($here -and (Test-Path $here)) { Copy-Item $here "$target.new" -Force }
else { Invoke-WebRequest -UseBasicParsing -OutFile "$target.new" $voiceUrl }
if ((Get-FileHash "$target.new" -Algorithm SHA256).Hash.ToLowerInvariant() -ne $voiceSha) {
    Remove-Item "$target.new"
    Die "The voice file didn't match its checksum, so it wasn't installed."
}
Move-Item "$target.new" $target -Force

$log = Join-Path $dir 'server.log'
$run = Join-Path $dir 'run.cmd'
@"
@echo off
cd /d "$kokoro"
set USE_GPU=false
set PYTHONUTF8=1
set PYTHONPATH=$kokoro;$kokoro\api
set MODEL_DIR=src/models
set VOICES_DIR=src/voices/v1_0
set WEB_PLAYER_PATH=$kokoro\web
set HF_HUB_DISABLE_TELEMETRY=1
.venv\Scripts\uvicorn.exe api.src.main:app --host 127.0.0.1 --port $port >> "$log" 2>&1
"@ | Set-Content -Encoding ASCII $run

if (-not $test) {
    $hidden = Join-Path $dir 'run-hidden.vbs'
    "CreateObject(""WScript.Shell"").Run """"""$run"""""", 0, False" | Set-Content -Encoding ASCII $hidden
    Copy-Item $hidden (Join-Path ([Environment]::GetFolderPath('Startup')) 'Capri voice.vbs') -Force
}
$server = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "`"$run`"" -WindowStyle Hidden -PassThru

try {
    Say 'Waiting for it to start...'
    for ($i = 0; $i -lt 180 -and -not (HasVoice); $i++) { Start-Sleep -Seconds 2 }
    if (-not (HasVoice)) { Die "It didn't start. Its log is at $log." }
    $wav = Join-Path $dir 'test.wav'
    $body = "{`"model`":`"kokoro`",`"voice`":`"$voice`",`"input`":`"Hey choom. Can you hear me?`",`"response_format`":`"wav`"}"
    try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 300 -Method Post -ContentType 'application/json' -Body $body -OutFile $wav "http://127.0.0.1:$port/v1/audio/speech" }
    catch { Die "It started but couldn't speak. Its log is at $log." }
    $bytes = [IO.File]::ReadAllBytes($wav)
    if ($bytes.Length -le 10000 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'RIFF') { Die "It started but its speech came back wrong. Its log is at $log." }
    Say "Capri's voice works. A sample is at $wav."
    if (-not $test) { Say "A new Capri uses it by itself. Otherwise: open her Mainframe, VOICE, CUSTOM LOCAL, Set up, and pick Capri's voice." }
}
finally {
    if ($test) { taskkill /PID $server.Id /T /F 2>$null | Out-Null }
}
