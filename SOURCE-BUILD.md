# Build FLICK 1.0.0

Requirements: .NET 10 SDK, Windows x64, MSYS2 UCRT64 GCC/G++, make, Git,
pkg-config, tar and patch. Use a path without spaces for the native build.
This archive contains the application, native bridge/filter, original queue
headers, pinned dependency archives and their build recipes. Application tests,
recordings and local preferences are excluded.

1. In MSYS2 UCRT64, run `bash prepare-gold-deps.sh`. It checks pinned hashes,
   extracts packaged dependencies, builds x264 and installs NVIDIA headers.
2. In PowerShell, run `./build-gold.ps1 -OutputDirectory C:/build/FLICK-1.0.0`.
   Set `-MsysRoot` and `-DotNet` if needed. This builds native tools into a fresh
   directory, embeds their hashes, then publishes a self-contained Windows x64 app.
   Existing canonical tools are never overwritten.
3. To rebuild only the managed application using already prepared tools:

   ```powershell
   dotnet publish FLICK.csproj -c Release -r win-x64 --self-contained true -o C:/build/FLICK-1.0.0
   ```

The baseline worker, profile worker and editor tools use distinct configuration
scripts in native-capture. Profile scaling uses scale-d3d11-leases.patch. The
transcoder is a separately named copy of the profile worker. Queue checksums are
checked before and after the native build. The pool owns eight textures and uses
one GPU-side copy per accepted frame, with no CPU readback in recording.

NAudio 2.2.1 is restored from NuGet; Inter fonts are embedded. SDK 10.0.403 and
.NET runtime 10.0.14 were used for this release. The runtime's license and notices
are supplied with the binaries. GPU drivers are not redistributed.

## Setup wizard

Install WiX 6.0.2 through its .NET tool and add the UI extension:

```powershell
dotnet tool install wix --version 6.0.2 --tool-path ./build-tools
./build-tools/wix.exe extension add WixToolset.UI.wixext/6.0.2
```

Create a clean payload containing the complete published app, its native tools,
README, LICENSE, dependency/runtime notices and corresponding source ZIP. Include
no local preferences, recordings, tests, PDBs or build logs. Then run:

```powershell
./installer/build-setup.ps1 -PayloadDirectory C:/build/payload -OutputFile C:/build/FLICK-V1-Setup.msi -Wix ./build-tools/wix.exe
```

The installer is per-user and uses Windows Installer rollback support. A native
guard checks the application mutex and refuses installation, repair or removal
while a recorder runs. It does not terminate recording or remove user data.
Changes to native tools require regenerating GoldToolHashes.json and rebuilding.

Upstream source archives retain their original contents and licenses, including
upstream build/test material. These are the corresponding sources of redistributed
dependencies, rather than FLICK development artifacts.
