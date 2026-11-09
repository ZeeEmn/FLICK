# Third-party components and source

FLICK's original C# application/UI code is GPL-3.0-only, copyright ZeeEmn.
See LICENSE. Separately licensed files retain their own notices. Complete
corresponding source is included in source/FLICK-V1-SOURCE.zip in binary packages,
and is also available as a separate release asset and in the repository.

## Native media tools

FFmpeg commit 946fcce07b6dcd0331c8cc609192aeff5e1924f8 supplies ffmpeg.exe,
ffprobe.exe, rewind-desktop-capture.exe, flick-profile-capture.exe and the
separately named flick-transcoder.exe. These builds are GPL-2.0-or-later because
libx264 is enabled. Their networking is disabled; only file/pipe protocols are
enabled. Internal worker/bridge names are retained for compatibility.

The source includes the pinned FFmpeg archive, configure/build scripts, added
capture filter, native bridge, unchanged queue headers and scaler lease patch.
FFmpeg's license texts are included with the binaries. Native bridge/filter/queue
files retain their LGPL-2.1-or-later notices. The screenshot helper is GPL-3.0-only.

Encoder dependencies:

- x264 commit b35605ace3ddf7c1a5d67a2eb553f034aef41d55: GPL-2.0-or-later.
  Its complete MSYS2 source archive and build recipe are included. FLICK builds a
  static library with the CLI and assembly disabled.
- Intel libvpl 2.17.0: MIT. Exact static library package, full source archive,
  build recipe/patches and notices are included.
- AMD AMF 1.5.2 headers: MIT. Incorporated headers, exact package and license are
  included. Unrelated AMD SDK samples/media are excluded.
- nv-codec-headers n13.0.19.0: MIT. Pinned source archive and checksum included.
- GCC/libstdc++ runtime: GPLv3 with GCC Runtime Library Exception. The relevant
  GCC, mingw-w64 and winpthreads notices are included in licenses/runtime.

GPU drivers are supplied by the user's system, not redistributed here.

## Managed application and fonts

- NAudio 2.2.1 and its component assemblies: MIT, copyright Mark Heath and
  contributors. License and upstream source archive are included.
- Inter 4.1: SIL Open Font License 1.1, Rasmus Andersson and contributors.
  Original fonts are embedded; the OFL license is included.
- Microsoft .NET 10.0.14 and Windows Desktop Runtime are redistributed with the
  self-contained app. Their license terms and notices are in licenses/dotnet.
  Sources: https://github.com/dotnet/runtime/tree/v10.0.14 and
  https://github.com/dotnet/wpf/tree/v10.0.14 .

## Building

See SOURCE-BUILD.md and build-gold.ps1. Native recipes live in native-capture;
profile scaling uses desktop-capture/scale-d3d11-leases.patch. The profile worker
and flick-transcoder.exe are byte-identical and independently hash-checked.
The embedded GoldToolHashes.json verifies the native tools at startup.

Upstream source archives retain their original contents and licenses. No
ScreenRecorderLib, game injection module or WGC test module is included in V1.
