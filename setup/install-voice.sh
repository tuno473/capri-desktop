#!/usr/bin/env bash
# Installs Capri's voice on Linux or Mac: Kokoro-FastAPI, its model, and the voice, answering on this computer only.
# It needs git and curl, and about 3 GB. Run it again at any time; it picks up where it stopped.
#   CAPRI_VOICE_DIR   where to install it (default ~/.local/share/capri-voice)
#   CAPRI_VOICE_PORT  the port (default 8880, the one Capri expects)
#   CAPRI_VOICE_TEST  1 starts it, checks it speaks and stops it, without setting it to start at login
set -euo pipefail

KOKORO_TAG=v0.9.2
KOKORO_COMMIT=06902e7ea0f51eed396bd3d253864f44218fec81
VOICE=af_cortes_tuned
VOICE_SHA=9a183d7f71a9c5dbea3b83750c7e9cc9e17c13ba80b9f0dca6e7be20514c4100
VOICE_URL="https://raw.githubusercontent.com/tuno473/capri-desktop/main/voices/$VOICE.pt"
DIR="${CAPRI_VOICE_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/capri-voice}"
PORT="${CAPRI_VOICE_PORT:-8880}"
TEST="${CAPRI_VOICE_TEST:-0}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || true)"

say() { printf '\033[1;35mcapri voice\033[0m %s\n' "$*"; }
die() { say "$*" >&2; exit 1; }
sha() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
has_voice() { curl -sf -m 5 "http://127.0.0.1:$PORT/v1/audio/voices" 2>/dev/null | grep -q "\"$VOICE\""; }

command -v git >/dev/null || die "It needs git. Install it and run this again."
command -v curl >/dev/null || die "It needs curl. Install it and run this again."

if [ "$TEST" != 1 ] && has_voice; then
    say "Capri's voice is already answering on port $PORT."
    exit 0
fi
if curl -s -m 3 -o /dev/null "http://127.0.0.1:$PORT/"; then
    die "Something else is using port $PORT. Close it, or set CAPRI_VOICE_PORT and tell Capri the new address."
fi

export PATH="$HOME/.local/bin:$PATH"
if ! command -v uv >/dev/null; then
    say "Installing uv, which sets up Kokoro's Python..."
    curl -LsSf https://astral.sh/uv/install.sh | sh
    export PATH="$HOME/.local/bin:$PATH"
fi

mkdir -p "$DIR"
KOKORO="$DIR/kokoro"
if [ ! -d "$KOKORO/.git" ]; then
    say "Downloading Kokoro-FastAPI $KOKORO_TAG..."
    rm -rf "${KOKORO:?}.partial"
    git -c advice.detachedHead=false clone -q --depth 1 --branch "$KOKORO_TAG" https://github.com/remsky/Kokoro-FastAPI.git "$KOKORO.partial"
    [ "$(git -C "$KOKORO.partial" rev-parse HEAD)" = "$KOKORO_COMMIT" ] || die "Kokoro-FastAPI $KOKORO_TAG isn't the version this was tried with. Nothing was installed."
    mv "$KOKORO.partial" "$KOKORO"
fi
cd "$KOKORO"

say "Installing Kokoro's Python packages (this is the big download)..."
# Kokoro's own lockfile, so everyone gets the exact versions it was tried with.
uv sync -q --frozen --extra cpu

say "Downloading Kokoro's model..."
.venv/bin/python docker/scripts/download_model.py --output api/src/models/v1_0

TARGET="api/src/voices/v1_0/$VOICE.pt"
if [ -n "$HERE" ] && [ -f "$HERE/../voices/$VOICE.pt" ]; then cp "$HERE/../voices/$VOICE.pt" "$TARGET.new"
else curl -fsSL -o "$TARGET.new" "$VOICE_URL"; fi
[ "$(sha "$TARGET.new")" = "$VOICE_SHA" ] || { rm -f "$TARGET.new"; die "The voice file didn't match its checksum, so it wasn't installed."; }
mv "$TARGET.new" "$TARGET"

RUN="$DIR/run.sh"
cat > "$RUN" <<RUNNER
#!/usr/bin/env bash
cd "$KOKORO"
export USE_GPU=false PYTHONPATH="$KOKORO:$KOKORO/api" MODEL_DIR=src/models VOICES_DIR=src/voices/v1_0 WEB_PLAYER_PATH="$KOKORO/web" HF_HUB_DISABLE_TELEMETRY=1
exec .venv/bin/uvicorn api.src.main:app --host 127.0.0.1 --port $PORT
RUNNER
chmod +x "$RUN"

SERVER=""
LOG="$DIR/server.log"
if [ "$TEST" = 1 ]; then
    "$RUN" >"$LOG" 2>&1 &
    SERVER=$!
elif [ "$(uname)" = Darwin ]; then
    PLIST="$HOME/Library/LaunchAgents/com.capri.voice.plist"
    mkdir -p "$(dirname "$PLIST")"
    cat > "$PLIST" <<AGENT
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Label</key><string>com.capri.voice</string>
<key>ProgramArguments</key><array><string>$RUN</string></array>
<key>RunAtLoad</key><true/>
<key>KeepAlive</key><true/>
<key>StandardOutPath</key><string>$LOG</string>
<key>StandardErrorPath</key><string>$LOG</string>
</dict></plist>
AGENT
    launchctl unload "$PLIST" 2>/dev/null || true
    launchctl load "$PLIST"
elif systemctl --user show-environment >/dev/null 2>&1; then
    UNIT="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/capri-voice.service"
    mkdir -p "$(dirname "$UNIT")"
    cat > "$UNIT" <<SERVICE
[Unit]
Description=Capri's voice (Kokoro-FastAPI on 127.0.0.1:$PORT)
After=network.target

[Service]
ExecStart=$RUN
Restart=on-failure
RestartSec=5
Nice=5

[Install]
WantedBy=default.target
SERVICE
    systemctl --user daemon-reload
    systemctl --user enable --now capri-voice.service
else
    say "No systemd here, so it won't start by itself at login. Start it with: $RUN"
    nohup "$RUN" >"$LOG" 2>&1 &
fi

finish() { if [ -n "$SERVER" ]; then kill "$SERVER" 2>/dev/null || true; fi; }
trap finish EXIT

say "Waiting for it to start..."
for _ in $(seq 1 180); do
    has_voice && break
    sleep 2
done
has_voice || die "It didn't start. Its log is at $LOG (or: journalctl --user -u capri-voice)."

WAV="$DIR/test.wav"
curl -sf -m 300 -o "$WAV" -H 'Content-Type: application/json' \
    -d "{\"model\":\"kokoro\",\"voice\":\"$VOICE\",\"input\":\"Hey choom. Can you hear me?\",\"response_format\":\"wav\"}" \
    "http://127.0.0.1:$PORT/v1/audio/speech" || die "It started but couldn't speak. Its log is at $LOG."
[ "$(head -c 4 "$WAV")" = RIFF ] && [ "$(wc -c < "$WAV")" -gt 10000 ] || die "It started but its speech came back wrong. Its log is at $LOG."
say "Capri's voice works. A sample is at $WAV."
[ "$TEST" = 1 ] || say "A new Capri uses it by itself. Otherwise: right-click her > Capri Mainframe > Use Capri's voice."
