#!/usr/bin/env bash
set -euo pipefail

cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../web/src"
mkdir -p calls

render() {
  "${ESPEAK_NG:-espeak-ng}" -v "$1" -s 152 --stdout "$2" |
    ffmpeg -nostdin -hide_banner -loglevel error -y -i pipe:0 \
      -af 'highpass=f=95,lowpass=f=7200,silenceremove=start_periods=1:start_duration=0.012:start_threshold=-48dB:start_silence=0.015:stop_periods=1:stop_duration=0.11:stop_threshold=-48dB,acompressor=threshold=0.12:ratio=2:attack=5:release=55:makeup=1.65,alimiter=limit=0.74:level=0:latency=1,apad=pad_dur=0.055' \
      -ac 1 -ar 24000 -map_metadata -1 -c:a libmp3lame -b:a 48k "calls/$3.mp3"
}

render cmn+m3 吃 chi-lower
render cmn+m3 碰 peng-lower
render cmn+m3 胡 hu-lower
render cmn+f2 吃 chi-higher
render cmn+f2 碰 peng-higher
render cmn+f2 胡 hu-higher
render en-us+m3 Chow chow
