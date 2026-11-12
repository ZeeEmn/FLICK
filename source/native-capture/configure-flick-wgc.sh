#!/usr/bin/env bash
set -eu
export PATH=/ucrt64/bin:/usr/bin:$PATH
cd "$(dirname "$0")"
root="$PWD"
# Keep assertion/source locations reproducible without publishing the builder's home path.
export CFLAGS="${CFLAGS:-} -ffile-prefix-map=$(cygpath -m "$root")=/usr/src/rewind/native-capture -ffile-prefix-map=$root=/usr/src/rewind/native-capture"
src="$root/FFmpeg-946fcce07b6dcd0331c8cc609192aeff5e1924f8"
cp ../wgc-capture/vsrc_rewindwgc.c "$src/libavfilter/"
cp ../wgc-capture/bridge_api.h "$src/libavfilter/rewind_wgc_bridge_api.h"
cp rewind_queue.h rewind_async.h "$src/libavfilter/"
if ! grep -q 'ff_vsrc_rewindwgc;' "$src/libavfilter/allfilters.c"; then
    sed -i '/extern const FFFilter ff_vsrc_ddagrab;/a extern const FFFilter ff_vsrc_rewindwgc;' "$src/libavfilter/allfilters.c"
fi
if ! grep -q 'CONFIG_REWINDWGC_FILTER' "$src/libavfilter/Makefile"; then
    sed -i '/OBJS-$(CONFIG_DDAGRAB_FILTER)/a OBJS-$(CONFIG_REWINDWGC_FILTER) += vsrc_rewindwgc.o' "$src/libavfilter/Makefile"
fi
if ! grep -q '^rewindwgc_filter_deps=' "$src/configure"; then
    sed -i '/^ddagrab_filter_deps=/a rewindwgc_filter_deps="d3d11va"' "$src/configure"
fi
if ! grep -q 'FlickScaleLease' "$src/libavfilter/vf_scale_d3d11.c"; then
    (cd "$src" && patch -p1 < "$root/../desktop-capture/scale-d3d11-leases.patch")
fi
mkdir -p build-flick-v12-wgc
cd build-flick-v12-wgc
export PKG_CONFIG_PATH="$root/../gold-deps/ucrt64/lib/pkgconfig:$root/headers/lib/pkgconfig"
if [ "$#" -eq 0 ] || [ "$1" != "--reuse-config" ]; then
../FFmpeg-946fcce07b6dcd0331c8cc609192aeff5e1924f8/configure \
  --target-os=mingw32 --arch=x86_64 --prefix=/rewind-gold-build \
  --cc=gcc --pkg-config=/ucrt64/bin/pkg-config --pkg-config-flags=--static \
  --extra-cflags="${REWIND_GOLD_CFLAGS:--DREWIND_PRODUCTION -DREWIND_RELEASE_QUIET} -I../headers/include -I../../gold-deps/ucrt64/include" --extra-ldflags="-static -L../../gold-deps/ucrt64/lib" --extra-libs="-lstdc++" \
  --disable-autodetect --disable-x86asm --disable-doc --disable-debug \
  --disable-network --disable-everything --enable-ffprobe --enable-gpl --enable-amf --enable-libvpl --enable-libx264 \
  --enable-ffmpeg --enable-avcodec --enable-avformat --enable-avfilter --enable-swscale \
  --enable-ffnvcodec --enable-nvenc --enable-d3d11va \
  --enable-encoder=h264_nvenc,hevc_nvenc,h264_amf,hevc_amf,h264_qsv,hevc_qsv,libx264,wrapped_avframe,aac,pcm_s16le,mjpeg,rawvideo --enable-muxer=mp4,null,wav,image2,rawvideo --enable-demuxer=mov,wav,rawvideo --enable-decoder=h264,hevc,aac,pcm_s16le,mjpeg,rawvideo,wrapped_avframe --enable-parser=h264,hevc,aac \
  --enable-protocol=file,pipe \
  --enable-filter=scale_d3d11,rewindwgc,testsrc2,color,sine,format,hwupload,hwdownload,setpts,settb,scale,crop,null,amix,aresample,aformat,anull \
  --enable-indev=lavfi
# FFmpeg make does not consistently notice changed environment compiler flags.
# A fresh configure must not reuse objects built with a previous privacy/profile map.
make clean
fi
make -j4 ffmpeg.exe ffprobe.exe
