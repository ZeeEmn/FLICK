$ErrorActionPreference = 'Stop'
$installer = $env:FLICK_UPDATE_MSI
$oldExe = $env:FLICK_UPDATE_EXECUTABLE
$code = -1
$reopen = $oldExe
$appExited = $false
try {
    $parent = Get-Process -Id ([int]$env:FLICK_UPDATE_PID) -ErrorAction SilentlyContinue
    if ($parent -and $parent.StartTime.ToUniversalTime().ToFileTimeUtc() -eq [long]$env:FLICK_UPDATE_START) {
        if (!$parent.WaitForExit(60000)) { throw 'FLICK did not exit in time.' }
    }
    $appExited = $true
    $folder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\FLICK'
    $registration = Get-ItemProperty -LiteralPath 'HKCU:\Software\FLICK\Installer' -ErrorAction SilentlyContinue
    if ($registration.Installed -eq 1) {
        $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'FLICK\FLICK.lnk'
        if ([IO.File]::Exists($shortcut)) {
            $shell = New-Object -ComObject WScript.Shell
            $target = $shell.CreateShortcut($shortcut).TargetPath
            if ([IO.Path]::GetFileName($target) -ine 'FLICK.exe' -or ![IO.File]::Exists($target)) {
                throw 'Could not locate the installed FLICK executable.'
            }
            $folder = [IO.Path]::GetDirectoryName($target)
        } elseif (![IO.File]::Exists((Join-Path $folder 'FLICK.exe'))) {
            throw 'Could not locate the installed FLICK folder.'
        }
    }
    $file = [IO.File]::Open($installer,'Open','Read','Read')
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($sha.ComputeHash($file)).Replace('-','') }
        finally { $sha.Dispose() }
        if ($digest -ine $env:FLICK_UPDATE_SHA256) { throw 'The verified installer changed before launch.' }
        $startup = if ($env:FLICK_UPDATE_STARTUP -eq '1') { '1' } else { '0' }
        $desktop = if ($env:FLICK_UPDATE_DESKTOP -eq '1') { '1' } else { '0' }
        $quotedFolder = $folder -replace '\\+$', '$0$0'
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'
        $start.Arguments = '/i "'+$installer+'" /quiet /norestart STARTONLOGIN='+$startup+' DESKTOPSHORTCUT='+$desktop+' INSTALLFOLDER="'+$quotedFolder+'"'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $setup = [Diagnostics.Process]::Start($start)
        try { $setup.WaitForExit(); $code = $setup.ExitCode }
        finally { $setup.Dispose() }
        if ($code -in 0,3010) { $reopen = Join-Path $folder 'FLICK.exe' }
    } finally { $file.Dispose() }
} finally {
    try { [IO.File]::WriteAllText($env:FLICK_UPDATE_RESULT, [string]$code) } catch {}
    try { [IO.File]::Delete($installer); [IO.Directory]::Delete([IO.Path]::GetDirectoryName($installer)) } catch {}
    if ($appExited -and [IO.File]::Exists($reopen)) {
        try { Start-Process -FilePath $reopen -WorkingDirectory ([IO.Path]::GetDirectoryName($reopen)) } catch {}
    }
}
