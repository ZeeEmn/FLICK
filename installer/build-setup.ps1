param(
    [Parameter(Mandatory=$true)][string]$PayloadDirectory,
    [Parameter(Mandatory=$true)][string]$OutputFile,
    [string]$Wix='wix',
    [string]$Compiler='C:/msys64/ucrt64/bin/g++.exe'
)
$ErrorActionPreference='Stop'
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payload=[IO.Path]::GetFullPath($PayloadDirectory)
$output=[IO.Path]::GetFullPath($OutputFile)
if(Test-Path -LiteralPath $output){throw 'Choose a fresh installer output path'}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('FLICK-setup-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$guard=Join-Path $temp 'setup-guard.dll'
$env:PATH=(Split-Path ([IO.Path]::GetFullPath($Compiler)) -Parent)+';'+$env:PATH
& $Compiler -shared -std=c++17 -O2 -s -static -static-libgcc -static-libstdc++ (Join-Path $PSScriptRoot 'guard.cpp') -o $guard -lmsi
if($LASTEXITCODE -ne 0){throw 'Setup guard compilation failed'}
function Xml([string]$value){[Security.SecurityElement]::Escape($value)}
$xml=[Text.StringBuilder]::new()
[void]$xml.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Fragment><DirectoryRef Id="INSTALLFOLDER">')
$files=Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName
$refs=[Collections.Generic.List[string]]::new()
foreach($file in $files){
    $relative=$file.FullName.Substring($payload.Length+1)
    if($relative -match '(^|[\\/])(data|logs|obj|bin|\.cache|wgc-tests|desktop-tests)([\\/]|$)|\.(mp4|fmp4|pdb|log)$|^(settings|preferences)\.json$'){throw "Unexpected payload: $relative"}
        $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($relative.ToLowerInvariant())
        $hash = $sha.ComputeHash($bytes)
        $suffix = [BitConverter]::ToString($hash).Replace('-','').Substring(0,24)
    }
    finally {
        $sha.Dispose()
    }
    $component='C'+$suffix;$fileId='F'+$suffix;$refs.Add($component)
    $directory=[IO.Path]::GetDirectoryName($relative)
    $subdir=if($directory){' Subdirectory="'+(Xml $directory)+'"'}else{''}
    [void]$xml.AppendLine('<Component Id="'+$component+'" Guid="*"'+$subdir+'><File Id="'+$fileId+'" Source="'+(Xml $file.FullName)+'" KeyPath="yes" /></Component>')
}
[void]$xml.AppendLine('</DirectoryRef><ComponentGroup Id="Payload">')
foreach($id in $refs){[void]$xml.AppendLine('<ComponentRef Id="'+$id+'" />')}
[void]$xml.AppendLine('</ComponentGroup></Fragment></Wix>')
$harvest=Join-Path $temp 'Payload.wxs';[IO.File]::WriteAllText($harvest,$xml.ToString())
& $Wix build (Join-Path $PSScriptRoot 'FLICK.wxs') (Join-Path $PSScriptRoot 'Wizard.wxs') $harvest -arch x64 -ext WixToolset.UI.wixext/6.0.2 -d "SourceRoot=$sourceRoot" -d "GuardDll=$guard" -pdbtype none -intermediatefolder $temp -out $output
if($LASTEXITCODE -ne 0){throw 'MSI build failed'}
Write-Output "Setup wizard ready: $output"
