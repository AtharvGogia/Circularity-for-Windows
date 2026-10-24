#!/usr/bin/env bash
# Builds the slim, LGPL-licensed ffmpeg.exe that ships with Circularity.
# Runs inside MSYS2's UCRT64 environment; start it with build-ffmpeg.ps1.
#
# Only the encoders, containers and filters Circularity uses are enabled, but every decoder and
# demuxer is kept so it can still read almost any video or audio file. Nothing GPL is included:
# H.264 is encoded by Windows' own Media Foundation encoder instead of x264.
set -euo pipefail

FFMPEG_VERSION="${FFMPEG_VERSION:-8.1.2}"
HERE="$(cd "$(dirname "$0")" && pwd)"
# FFmpeg's build breaks on paths with spaces or quotes, so build in a plain folder inside MSYS2
# and copy only the results back.
WORK=/tmp/circularity-ffmpeg
BUILD_OUT="$WORK/out"
OUT="$HERE/out"

echo "==> Installing build tools and codec libraries (MSYS2 packages)"
pacman -S --needed --noconfirm \
    make nasm diffutils \
    mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-pkgconf \
    mingw-w64-ucrt-x86_64-zlib mingw-w64-ucrt-x86_64-libvpx mingw-w64-ucrt-x86_64-lame \
    mingw-w64-ucrt-x86_64-opus mingw-w64-ucrt-x86_64-libogg mingw-w64-ucrt-x86_64-libvorbis \
    mingw-w64-ucrt-x86_64-dav1d

mkdir -p "$WORK"
cd "$WORK"
if [ ! -d "ffmpeg-$FFMPEG_VERSION" ]; then
    echo "==> Downloading FFmpeg $FFMPEG_VERSION source"
    curl -fL --retry 3 -o "ffmpeg-$FFMPEG_VERSION.tar.xz" "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz"
    tar -xf "ffmpeg-$FFMPEG_VERSION.tar.xz"
fi
cd "ffmpeg-$FFMPEG_VERSION"

echo "==> Configuring"
./configure \
    --prefix="$BUILD_OUT" \
    --arch=x86_64 --target-os=mingw32 \
    --enable-static --disable-shared \
    --pkg-config-flags=--static --extra-ldflags=-static \
    --disable-debug --disable-doc --disable-network --disable-autodetect \
    --disable-programs --enable-ffmpeg \
    --enable-zlib --enable-mediafoundation \
    `# The Media Foundation (Windows) encoders are built against Direct3D 11; d3d11.dll is part of Windows.` \
    --enable-d3d11va \
    --enable-libvpx --enable-libmp3lame --enable-libopus --enable-libvorbis --enable-libdav1d \
    --disable-encoders \
    --enable-encoder=h264_mf,aac,libmp3lame,flac,pcm_s16le,libvorbis,libopus,libvpx_vp9,gif,png \
    `# wrapped_avframe lets "-f null" decode a file end to end, which is how outputs are checked.` \
    --enable-encoder=wrapped_avframe \
    --disable-muxers \
    --enable-muxer=mp4,mov,ipod,matroska,webm,gif,mp3,wav,flac,ogg,image2,null \
    --disable-filters \
    --enable-filter=scale,crop,fps,split,palettegen,paletteuse,format,aformat,aresample,null,anull \
    --enable-filter=setsar,setpts,asetpts,transpose,hflip,vflip,buffer,abuffer,buffersink,abuffersink \
    --enable-filter=testsrc,testsrc2,sine,noise \
    --disable-devices --enable-indev=lavfi \
    --disable-protocols --enable-protocol=file,pipe

echo "==> Building"
make -j"$(nproc)"
make install
strip "$BUILD_OUT/bin/ffmpeg.exe"

rm -rf "$OUT"
mkdir -p "$OUT/bin" "$OUT/licenses"
cp "$BUILD_OUT/bin/ffmpeg.exe" "$OUT/bin/"

# Licence texts that must ship alongside the binary. MSYS2 only packages some of them; the rest
# (libvpx, LAME, libvorbis, zlib) are kept in build/installer/licenses.
cp COPYING.LGPLv2.1 "$OUT/licenses/FFmpeg-LGPL-2.1.txt"
for pkg in opus libogg dav1d; do
    src="/ucrt64/share/licenses/$pkg"
    if [ -d "$src" ]; then
        for f in "$src"/*; do cp "$f" "$OUT/licenses/$pkg-$(basename "$f").txt"; done
    else
        echo "warning: no licence files found for $pkg" >&2
    fi
done
echo "$FFMPEG_VERSION" > "$OUT/VERSION"

echo "==> Done: $OUT/bin/ffmpeg.exe ($(du -h "$OUT/bin/ffmpeg.exe" | cut -f1))"
echo "==> DLLs it needs (should only be Windows system DLLs):"
objdump -p "$BUILD_OUT/bin/ffmpeg.exe" | grep "DLL Name" | sort -u
