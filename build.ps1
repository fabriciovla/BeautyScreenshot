# Compiles BeautyScreenshot.exe and BeautyScreenshotSetup.exe with the Roslyn compiler that ships with Visual Studio
# or its Build Tools, against .NET Framework 4.8 (no .NET SDK needed).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath }
$csc = if ($vs) { Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe' }
if (-not $csc -or -not (Test-Path $csc)) { throw 'Visual Studio or its Build Tools (with MSBuild) are required.' }
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null

function Compile([string[]] $arguments) {
    & $csc /nologo /noconfig /langversion:latest @arguments
    if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
}

$refs = @(
    "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Xml.dll", "$fw\System.Xaml.dll",
    "$fw\System.Drawing.dll", "$fw\System.Windows.Forms.dll",
    "$fw\WPF\WindowsBase.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll"
) | ForEach-Object { "/r:$_" }

# 1. The icon: src\AppIcon.ico for the .exe files, src\AppIcon.png embedded for the windows (IconArt.AppIcon).
$ico = "$root\src\AppIcon.ico"
$png = "/resource:$root\src\AppIcon.png,AppIcon.png"

# 2. The app itself.
$sources = Get-ChildItem "$root\src\*.cs" | ForEach-Object FullName
Compile (@('/target:winexe', '/optimize+', '/platform:anycpu', '/warn:4',
    "/win32manifest:$root\src\app.manifest", "/win32icon:$ico", $png,
    "/resource:$root\src\SettingsWindow.xaml,SettingsWindow.xaml",
    "/resource:$root\src\shutter.wav,shutter.wav",
    "/out:$root\BeautyScreenshot.exe") + $refs + $sources)

# 3. The setup, carrying the app inside as a resource. It shares the install paths, the UI helpers and the icon.
$setupSources = (Get-ChildItem "$root\setup\*.cs" | ForEach-Object FullName) + @("$root\src\Install.cs", "$root\src\Lang.cs", "$root\src\Ui.cs", "$root\src\IconArt.cs")
Compile (@('/target:winexe', '/optimize+', '/platform:anycpu', '/warn:4',
    "/win32manifest:$root\src\app.manifest", "/win32icon:$ico", $png,
    "/resource:$root\setup\SetupStyles.xaml,SetupStyles.xaml",
    "/resource:$root\BeautyScreenshot.exe,BeautyScreenshot.exe",
    "/out:$root\BeautyScreenshotSetup.exe") + $refs + $setupSources)

Write-Host "OK -> $root\BeautyScreenshot.exe"
Write-Host "OK -> $root\BeautyScreenshotSetup.exe"
