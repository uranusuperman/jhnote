# Build jhnote.exe (.NET Framework 4.8 + WebView2 Core)
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -DownloadWebView2   # force (re)download of the SDK
#
# Requirements:
#   - Windows 10/11 with .NET Framework 4.8
#   - Roslyn csc: Visual Studio (any edition) OR .NET SDK (dotnet)
#   - WebView2 SDK assemblies: kept in .\lib (auto-downloaded from NuGet on first build)

param([switch]$DownloadWebView2)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$wvVersion = '1.0.3485.44'
$libDir = Join-Path $root 'lib'

# ---------------------------------------------------------------- compiler
$csc = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = (Get-ChildItem 'C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio' `
            -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like '*Roslyn*' } | Select-Object -First 1).FullName
}

$cscDll = $null
if (-not $csc) {
    $sdkRoot = 'C:\Program Files\dotnet\sdk'
    if (Test-Path $sdkRoot) {
        $cscDll = (Get-ChildItem $sdkRoot -Recurse -Filter csc.dll -ErrorAction SilentlyContinue |
                   Where-Object { $_.FullName -like '*Roslyn*bincore*' } |
                   Sort-Object FullName -Descending | Select-Object -First 1).FullName
    }
}
if (-not $csc -and -not $cscDll) { throw 'Roslyn csc not found (install Visual Studio or the .NET SDK)' }

function Invoke-Csc {
    param([string[]]$Arguments)
    if ($cscDll) { & dotnet $cscDll @Arguments } else { & $csc @Arguments }
}

# ------------------------------------------------------------ WebView2 SDK
function Resolve-WebView2 {
    param([switch]$Force)

    if (-not $Force) {
        if (Test-Path "$libDir\Microsoft.Web.WebView2.Core.dll") { return $libDir }

        $vs = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\PrivateAssemblies'
        if (-not (Test-Path "$vs\Microsoft.Web.WebView2.Core.dll")) {
            $hit = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio' `
                   -Recurse -Filter 'Microsoft.Web.WebView2.Core.dll' -ErrorAction SilentlyContinue |
                   Select-Object -First 1
            $vs = if ($hit) { Split-Path $hit.FullName } else { $null }
        }
        if ($vs -and (Test-Path "$vs\Microsoft.Web.WebView2.Core.dll")) { return $vs }
    }

    Write-Host "[..] downloading WebView2 SDK $wvVersion from nuget.org" -ForegroundColor Yellow
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("webview2_" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    $pkg = Join-Path $tmp 'pkg.zip'
    try {
        Invoke-WebRequest -UseBasicParsing -OutFile $pkg `
            -Uri "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$wvVersion"

        Expand-Archive -LiteralPath $pkg -DestinationPath $tmp -Force
        New-Item -ItemType Directory -Force -Path $libDir | Out-Null

        $core = Get-ChildItem $tmp -Recurse -Filter 'Microsoft.Web.WebView2.Core.dll' |
                Where-Object { $_.FullName -like '*\lib\net45\*' } | Select-Object -First 1
        if (-not $core) {
            $core = Get-ChildItem $tmp -Recurse -Filter 'Microsoft.Web.WebView2.Core.dll' | Select-Object -First 1
        }
        if (-not $core) { throw 'WebView2 Core dll not found in package' }

        $loader = Get-ChildItem $tmp -Recurse -Filter 'WebView2Loader.dll' |
                  Where-Object { $_.FullName -like '*win-x64*' } | Select-Object -First 1
        if (-not $loader) {
            $loader = Get-ChildItem $tmp -Recurse -Filter 'WebView2Loader.dll' | Select-Object -First 1
        }
        if (-not $loader) { throw 'WebView2Loader.dll not found in package' }

        Copy-Item $core.FullName   (Join-Path $libDir 'Microsoft.Web.WebView2.Core.dll') -Force
        Copy-Item $loader.FullName (Join-Path $libDir 'WebView2Loader.dll') -Force
        Write-Host "[OK] WebView2 SDK saved to $libDir" -ForegroundColor Green
        return $libDir
    }
    finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wv = Resolve-WebView2 -Force:$DownloadWebView2

# ------------------------------------------------------------------- build
New-Item -ItemType Directory -Force -Path "$root\bin" | Out-Null

# Stop running instances and drop hidden backups (<guid>_jhnote.exe) left by overwrite
Get-Process jhnote -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "$root\bin\*_jhnote.exe" -Force -ErrorAction SilentlyContinue

Invoke-Csc @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/langversion:latest', '/optimize+', '/utf8output',
    "/out:$root\bin\jhnote.exe",
    "/r:$fw\mscorlib.dll", "/r:$fw\System.dll", "/r:$fw\System.Core.dll",
    "/r:$fw\System.Windows.Forms.dll", "/r:$fw\System.Drawing.dll",
    "/r:$fw\System.Web.Extensions.dll",
    "/r:$wv\Microsoft.Web.WebView2.Core.dll",
    "$root\src\*.cs"
)
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

Copy-Item "$wv\Microsoft.Web.WebView2.Core.dll" "$root\bin\" -Force
Copy-Item "$wv\WebView2Loader.dll" "$root\bin\" -Force -ErrorAction SilentlyContinue

# Offline self-test (depends on Render.cs only, no network / WebView2)
Invoke-Csc @(
    '/nologo', '/target:exe', '/platform:anycpu', '/utf8output',
    "/out:$root\bin\selftest.exe",
    "/r:$fw\mscorlib.dll", "/r:$fw\System.dll",
    "$root\src\Render.cs", "$root\tests\SelfTest.cs"
)
if ($LASTEXITCODE -ne 0) { throw "SelfTest build failed (exit $LASTEXITCODE)" }

Write-Host "[OK] built $root\bin\jhnote.exe" -ForegroundColor Green
Get-ChildItem "$root\bin" | Select-Object Name, Length | Format-Table -AutoSize
