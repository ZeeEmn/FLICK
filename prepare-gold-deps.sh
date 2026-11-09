#!/usr/bin/env bash
set -eu
export PATH=/ucrt64/bin:/usr/bin:$PATH
cd "$(dirname "$0")"
root="$PWD"
cd gold-deps
sha256sum -c INPUT-SHA256.txt
for archive in packages/*.pkg.tar.zst; do tar -xf "$archive"; done
mkdir -p sources
if [ ! -d sources/mingw-w64-x264 ]; then tar -xf sources/mingw-w64-x264-0.165.r3222.b35605a-3.src.tar.zst -C sources; fi
if [ ! -d x264-src ]; then git clone --no-hardlinks sources/mingw-w64-x264/x264 x264-src; fi
git -C x264-src checkout --detach b35605ace3ddf7c1a5d67a2eb553f034aef41d55
cd x264-src
./configure --host=x86_64-w64-mingw32 --enable-static --disable-cli --disable-asm --prefix="$root/gold-deps/ucrt64"
make -j4
make install-lib-static
cd "$root"
for pc in gold-deps/ucrt64/lib/pkgconfig/*.pc; do sed -i "s|^prefix=.*|prefix=$root/gold-deps/ucrt64|" "$pc"; done
cd native-capture
sha256sum -c SOURCE-SHA256.txt
if [ ! -d FFmpeg-946fcce07b6dcd0331c8cc609192aeff5e1924f8 ]; then tar -xf ffmpeg-source.tar.gz; fi
mkdir -p headers-source
tar -xf nv-codec-headers.tar.gz -C headers-source
nvsrc=$(find headers-source -mindepth 1 -maxdepth 1 -type d | head -n1)
make -C "$nvsrc" PREFIX="$root/native-capture/headers" install
printf 'Prepared pinned local dependencies. Run build-gold.ps1 next.\n'
