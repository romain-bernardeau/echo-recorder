#!/usr/bin/env bash
# build.sh: audio-only FFmpeg for Echo (Windows x64), cross-compiled on Ubuntu with mingw-w64.
#
# License: LGPL v2.1 or later. FFmpeg is configured WITHOUT --enable-gpl / --enable-nonfree and
# links only LAME (LGPL). No video codec is compiled in: Echo only decodes audio (recordings and
# imported audio or video files, whose video stream is dropped), resamples, detects silences,
# mixes tracks and encodes MP3.
#
# Output in ./out: ffmpeg.exe, the license texts, BUILD-INFO.txt, and the exact sources used
# (FFmpeg + LAME tarballs + this script) so the release carries its complete corresponding source.
#
# Run by .github/workflows/ffmpeg-audio.yml. Locally: sudo apt-get install mingw-w64 nasm make xz-utils gnupg
set -euo pipefail

FFMPEG_VERSION=9.0.2
FFMPEG_SHA256=8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e
LAME_VERSION=3.100
LAME_SHA256=ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e

HOST=x86_64-w64-mingw32
ROOT="$(pwd)"
WORK="$ROOT/work"
PREFIX="$WORK/prefix"
OUT="$ROOT/out"
rm -rf "$WORK" "$OUT"
mkdir -p "$WORK" "$PREFIX" "$OUT/src"
cd "$WORK"

# -- sources (pinned sha256; FFmpeg also checked against its release signature) ------------
curl -fsSL -o "ffmpeg-$FFMPEG_VERSION.tar.xz" "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz"
curl -fsSL -o "ffmpeg-$FFMPEG_VERSION.tar.xz.asc" "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz.asc"
curl -fsSL -o "lame-$LAME_VERSION.tar.gz" "https://deb.debian.org/debian/pool/main/l/lame/lame_$LAME_VERSION.orig.tar.gz"
echo "$FFMPEG_SHA256  ffmpeg-$FFMPEG_VERSION.tar.xz" | sha256sum -c -
echo "$LAME_SHA256  lame-$LAME_VERSION.tar.gz" | sha256sum -c -
curl -fsSL https://ffmpeg.org/ffmpeg-devel.asc | gpg --batch --import
gpg --batch --verify "ffmpeg-$FFMPEG_VERSION.tar.xz.asc" "ffmpeg-$FFMPEG_VERSION.tar.xz"

tar xf "ffmpeg-$FFMPEG_VERSION.tar.xz"
tar xf "lame-$LAME_VERSION.tar.gz"

# -- LAME (static) ------------------------------------------------------------------------
cd "lame-$LAME_VERSION"
./configure --host=$HOST --prefix="$PREFIX" --enable-static --disable-shared \
            --disable-frontend --disable-decoder --disable-gtktest
make -j"$(nproc)"
make install
cd "$WORK"

# -- FFmpeg: only what Echo uses -------------------------------------------------------------
DEMUXERS=wav,w64,mp3,aac,mov,matroska,ogg,flac,asf,amr,avi,aiff,caf,ac3,eac3
DECODERS=pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_f32le,pcm_f32be,pcm_f64le,pcm_u8,pcm_alaw,pcm_mulaw
DECODERS=$DECODERS,adpcm_ms,adpcm_ima_wav,gsm_ms,mp3,mp3float,mp2,mp2float,aac,aac_latm,opus,vorbis,flac
DECODERS=$DECODERS,wmav1,wmav2,wmapro,amrnb,amrwb,alac,ac3,eac3
PARSERS=aac,aac_latm,mpegaudio,opus,vorbis,flac,ac3,amr
FILTERS=aresample,aformat,adelay,amix,silencedetect,anull,atrim,volume,apad

cd "ffmpeg-$FFMPEG_VERSION"
CONFIGURE_ARGS=(
  --prefix="$PREFIX" --arch=x86_64 --target-os=mingw32 --cross-prefix=$HOST-
  --enable-static --disable-shared --pkg-config-flags=--static
  --extra-cflags="-I$PREFIX/include" --extra-ldflags="-L$PREFIX/lib -static"
  --disable-everything --disable-autodetect --disable-network --disable-doc --disable-debug
  --disable-ffplay --disable-ffprobe --disable-avdevice --disable-swscale
  --enable-w32threads
  --enable-libmp3lame
  --enable-protocol=file,pipe
  --enable-demuxer=$DEMUXERS
  --enable-decoder=$DECODERS
  --enable-parser=$PARSERS
  --enable-encoder=pcm_s16le,libmp3lame
  --enable-muxer=wav,mp3,null
  --enable-filter=$FILTERS
)
./configure "${CONFIGURE_ARGS[@]}"
make -j"$(nproc)"
$HOST-strip ffmpeg.exe

# -- package -------------------------------------------------------------------------------
cp ffmpeg.exe "$OUT/"
cp COPYING.LGPLv2.1 "$OUT/FFMPEG-COPYING.LGPLv2.1.txt"
cp LICENSE.md "$OUT/FFMPEG-LICENSE.md"
cp "../lame-$LAME_VERSION/COPYING" "$OUT/LAME-COPYING.txt"
cd "$WORK"
cp "ffmpeg-$FFMPEG_VERSION.tar.xz" "lame-$LAME_VERSION.tar.gz" "$ROOT/build.sh" "$OUT/src/"

LICENSE_LINE=$(grep -m1 '^#define FFMPEG_LICENSE' "ffmpeg-$FFMPEG_VERSION/config.h" | cut -d'"' -f2)
if [ "$LICENSE_LINE" != "LGPL version 2.1 or later" ]; then
  echo "Unexpected license: $LICENSE_LINE" >&2; exit 1
fi
cat > "$OUT/BUILD-INFO.txt" <<EOF
Audio-only FFmpeg build for Echo (Windows x64)

FFmpeg $FFMPEG_VERSION  sha256 $FFMPEG_SHA256  https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz
LAME   $LAME_VERSION    sha256 $LAME_SHA256  https://deb.debian.org/debian/pool/main/l/lame/lame_$LAME_VERSION.orig.tar.gz
License: $LICENSE_LINE (FFmpeg LGPL v2.1 or later, LAME LGPL v2 or later). No GPL or nonfree component.
Built with: build.sh (in the src folder of this release), mingw-w64 on Ubuntu, GitHub Actions.

configure ${CONFIGURE_ARGS[*]}
EOF
cat "$OUT/BUILD-INFO.txt"
ls -l "$OUT" "$OUT/src"
