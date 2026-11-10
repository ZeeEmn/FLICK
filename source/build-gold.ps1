param([string]$MsysRoot='C:/msys64',[string]$OutputDirectory=(Join-Path $PSScriptRoot '../build/FLICK-1.1.0'),[switch]$ReuseNativeConfigure,[string]$DotNet='dotnet')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PSScriptRoot)
$output=[IO.Path]::GetFullPath($OutputDirectory)
$queue=Join-Path $root 'native-capture/rewind_queue.h'
$expected='CEBBF2D37A1164A00FE869254BF18D147D9397F2370B64456E5F9C983BBE28A2'
if((Get-FileHash $queue -Algorithm SHA256).Hash -ne $expected){throw 'Queue checksum mismatch'}
$toolDir=Join-Path $output 'tools';if(Test-Path -LiteralPath $toolDir){throw 'Choose a fresh OutputDirectory; existing release tools are never replaced'};New-Item -ItemType Directory $toolDir -Force | Out-Null
$env:PATH=(Join-Path $MsysRoot 'ucrt64/bin')+';'+$env:PATH
$compiler=Join-Path $MsysRoot 'ucrt64/bin/g++.exe'
$common=@('-std=c++17','-O2','-s','-Wall','-Wextra','-D_WIN32_WINNT=0x0A00','-DREWIND_PRODUCTION','-DREWIND_RELEASE_QUIET','-static','-static-libgcc','-static-libstdc++')
& $compiler @common -shared (Join-Path $root 'desktop-capture/desktop_bridge.cpp') -o (Join-Path $toolDir 'rewind_desktop_bridge.dll') -ld3d11 -ldxgi -ldxguid -luuid -luser32
if($LASTEXITCODE -ne 0){throw 'Production bridge build failed'}
& $compiler -std=c++17 -O2 -s -municode -static -static-libgcc -static-libstdc++ (Join-Path $root 'desktop-capture/screenshot.cpp') -o (Join-Path $toolDir 'flick-screenshot.exe') -ld3d11 -ldxgi -ldxguid -luuid -lwindowscodecs -lole32 -luser32
if($LASTEXITCODE -ne 0){throw 'Screenshot helper build failed'}
$script=(Join-Path $root 'native-capture/configure-gold-worker.sh').Replace('\','/');$script='/'+$script.Substring(0,1).ToLower()+$script.Substring(2)
$options=@();if($ReuseNativeConfigure){$options+='--reuse-config'}
& (Join-Path $MsysRoot 'usr/bin/bash.exe') $script @options
if($LASTEXITCODE -ne 0){throw 'Production video tool build failed'}
foreach($name in @('ffmpeg.exe','ffprobe.exe')){Copy-Item (Join-Path $root "native-capture/build-gold/$name") $toolDir -Force}
Copy-Item (Join-Path $toolDir 'ffmpeg.exe') (Join-Path $toolDir 'rewind-desktop-capture.exe') -Force
# Keep the baseline worker separate from the profile-capable GPU-scale worker.
$profileScript=(Join-Path $root 'native-capture/configure-flick-capture.sh').Replace('\','/');$profileScript='/'+$profileScript.Substring(0,1).ToLower()+$profileScript.Substring(2)
& (Join-Path $MsysRoot 'usr/bin/bash.exe') $profileScript @options
if($LASTEXITCODE -ne 0){throw 'Profile capture tool build failed'}
Copy-Item (Join-Path $root 'native-capture/build-flick-gold-131/ffmpeg.exe') (Join-Path $toolDir 'flick-profile-capture.exe')
# The downscale entry point is byte-identical to the verified profile worker; no second encoding pass.
Copy-Item (Join-Path $toolDir 'flick-profile-capture.exe') (Join-Path $toolDir 'flick-transcoder.exe')
# Media editing adds concat packet filters without changing the capture executable.
$mediaScript=(Join-Path $root 'native-capture/configure-flick-media.sh').Replace('\','/');$mediaScript='/'+$mediaScript.Substring(0,1).ToLower()+$mediaScript.Substring(2)
& (Join-Path $MsysRoot 'usr/bin/bash.exe') $mediaScript @options
if($LASTEXITCODE -ne 0){throw 'Media editing tool build failed'}
foreach($name in @('ffmpeg.exe','ffprobe.exe')){Copy-Item (Join-Path $root "native-capture/build-flick-media/$name") $toolDir -Force}

$hashes=@{};Get-ChildItem $toolDir -File | Where-Object {$_.Name -in @('ffmpeg.exe','ffprobe.exe','rewind-desktop-capture.exe','rewind_desktop_bridge.dll','flick-screenshot.exe','flick-profile-capture.exe','flick-transcoder.exe')} | ForEach-Object {$hashes[$_.Name]=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}
$hashes | ConvertTo-Json | Set-Content (Join-Path $root 'GoldToolHashes.json') -Encoding utf8
# Existing canonical tools are immutable; reuse only an exact byte-for-byte match.
function Copy-ImmutableTool([string]$Source,[string]$Destination){
    $target=Join-Path $Destination (Split-Path $Source -Leaf)
    if(Test-Path -LiteralPath $target){if((Get-FileHash -LiteralPath $Source).Hash -ne (Get-FileHash -LiteralPath $target).Hash){throw "Refusing to replace existing tool: $target"};return}
    Copy-Item -LiteralPath $Source -Destination $target
}
$prepared=Join-Path $root 'release-tools';New-Item -ItemType Directory $prepared -Force | Out-Null
foreach($name in @('ffmpeg.exe','ffprobe.exe','rewind-desktop-capture.exe','flick-screenshot.exe')){Copy-ImmutableTool (Join-Path $toolDir $name) $prepared}
$bridgePrepared=Join-Path $root 'bridge-tools/v1.3.1';New-Item -ItemType Directory $bridgePrepared -Force | Out-Null
Copy-ImmutableTool (Join-Path $toolDir 'rewind_desktop_bridge.dll') $bridgePrepared
$profilePrepared=Join-Path $root 'profile-tools/v1.3.1';New-Item -ItemType Directory $profilePrepared -Force | Out-Null
Copy-ImmutableTool (Join-Path $toolDir 'flick-profile-capture.exe') $profilePrepared
$transcoderPrepared=Join-Path $root 'release-tools';New-Item -ItemType Directory $transcoderPrepared -Force | Out-Null
Copy-ImmutableTool (Join-Path $toolDir 'flick-transcoder.exe') $transcoderPrepared
$env:DOTNET_CLI_HOME=Join-Path $root '.dotnet-home'
$props=@('-p:BaseIntermediateOutputPath=obj/gold/','-p:MSBuildProjectExtensionsPath=obj/gold/','-p:Platform=x64','-p:NuGetAudit=false')
& $DotNet restore (Join-Path $root 'FLICK.csproj') @props --packages (Join-Path $root '.packages')
if($LASTEXITCODE -ne 0){throw 'Managed dependency restore failed'}
& $DotNet publish (Join-Path $root 'FLICK.csproj') @props -c Release -r win-x64 --self-contained true --no-restore -p:UseSharedCompilation=false --disable-build-servers -o $output
if($LASTEXITCODE -ne 0){throw 'Consumer build failed'}
if((Get-FileHash $queue -Algorithm SHA256).Hash -ne $expected){throw 'Queue checksum changed'}
Write-Output "Consumer build ready: $output"
