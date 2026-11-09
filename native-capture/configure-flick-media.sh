#!/usr/bin/env bash
set -eu
export PATH=/ucrt64/bin:/usr/bin:$PATH
cd "$(dirname "$0")"
root="$PWD"
# Keep assertion/source locations reproducible without publishing the builder's home path.
export CFLAGS="${CFLAGS:-} -ffile-prefix-map=$(cygpath -m "$root")=/usr/src/rewind/native-capture -ffile-prefix-map=$root=/usr/src/rewind/native-capture"
src="$root/FFmpeg-946fcce07b6dcd0331c8cc609192aeff5e1924f8"
cp ../desktop-capture/vsrc_rewinddesktop.c "$src/libavfilter/"
cp ../desktop-capture/bridge_api.h "$src/libavfilter/rewind_desktop_bridge_api.h"
cp rewind_queue.h rewind_async.h "$src/libavfilter/"
if ! grep -q 'ff_vsrc_rewinddesktop;' "$src/libavfilter/allfilters.c"; then
    sed -i '/extern const FFFilter ff_vsrc_ddagrab;/a extern const FFFilter ff_vsrc_rewinddesktop;' "$src/libavfilter/allfilters.c"
fi
if ! grep -q 'CONFIG_REWINDDESKTOP_FILTER' "$src/libavfilter/Makefile"; then
    sed -i '/OBJS-$(CONFIG_DDAGRAB_FILTER)/a OBJS-$(CONFIG_REWINDDESKTOP_FILTER) += vsrc_rewinddesktop.o' "$src/libavfilter/Makefile"
fi
if ! grep -q '^rewinddesktop_filter_deps=' "$src/configure"; then
    sed -i '/^ddagrab_filter_deps=/a rewinddesktop_filter_deps="d3d11va"' "$src/configure"
fi
mkdir -p build-flick-media
cd build-flick-media
export PKG_CONFIG_PATH="$root/../gold-deps/ucrt64/lib/pkgconfig:$root/headers/lib/pkgconfig"
if [ "$#" -eq 0 ] || [ "$1" != "--reuse-config" ]; then
../FFmpeg-946fcce07b6dcd0331c8cc609192aeff5e1924f8/configure \
  --target-os=mingw32 --arch=x86_64 --prefix=/rewind-gold-build \
  --cc=gcc --pkg-config=/ucrt64/bin/pkg-config --pkg-config-flags=--static \
  --extra-cflags="${REWIND_GOLD_CFLAGS:--DREWIND_PRODUCTION} -I../headers/include -I../../gold-deps/ucrt64/include" --extra-ldflags="-static -L../../gold-deps/ucrt64/lib" --extra-libs="-lstdc++" \
  --disable-autodetect --disable-x86asm --disable-doc --disable-debug \
  --disable-network --disable-everything --enable-ffprobe --enable-gpl --enable-amf --enable-libvpl --enable-libx264 \
  --enable-ffmpeg --enable-avcodec --enable-avformat --enable-avfilter --enable-swscale \
  --enable-ffnvcodec --enable-nvenc --enable-d3d11va \
  --enable-encoder=h264_nvenc,hevc_nvenc,h264_amf,hevc_amf,h264_qsv,hevc_qsv,libx264,wrapped_avframe,aac,pcm_s16le,mjpeg,rawvideo --enable-muxer=mp4,null,wav,image2,rawvideo --enable-demuxer=mov,wav,rawvideo,concat,matroska,avi,asf,mpegps,mpegts,flv,ogg --enable-decoder=h264,hevc,aac,pcm_s16le,mjpeg,rawvideo,wrapped_avframe,vp8,vp9,av1,mpeg4,mpeg2video,wmv3,vc1,mp3,opus,vorbis,flac,ac3,eac3,pcm_f32le,pcm_s24le --enable-parser=h264,hevc,aac,vp8,vp9,av1,mpeg4video,mpegvideo,vc1,mpegaudio,opus,vorbis,flac,ac3 \
  --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb --enable-protocol=file,pipe \
  --enable-filter=rewinddesktop,testsrc2,color,sine,format,hwupload,hwdownload,setpts,settb,scale,crop,null,amix,volume,aresample,aformat,anull \
  --enable-indev=lavfi
# FFmpeg make does not consistently notice changed environment compiler flags.
# A fresh configure must not reuse objects built with a previous privacy/profile map.
make clean
fi
make -j4 ffmpeg.exe ffprobe.exe
