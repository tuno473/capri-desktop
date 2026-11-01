param([string]$OutputDirectory, [switch]$SmokeTest, [int]$SmokeSeconds = 60)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$wpf = Join-Path $framework 'WPF'
$winmd = Get-ChildItem -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata') -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d+(\.\d+)+$' } | Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'Windows.winmd' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not (Test-Path -LiteralPath $compiler)) { throw 'csc.exe not found. Capri needs the .NET Framework 4.8 that comes with Windows 10 and 11.' }
if (-not $winmd) { throw 'Windows.winmd not found. Install the Windows 10 or 11 SDK.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $root 'out\windows' }
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $out -Force | Out-Null
$app = Join-Path $out 'Capri.exe'
$setup = Join-Path $out 'Capri-Setup.exe'
$icon = Join-Path $root 'assets\chip.ico'
$shared = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/warn:4', ('/win32manifest:' + (Join-Path $root 'app\app.manifest')), ('/win32icon:' + $icon), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll')
$references = @('/reference:System.Runtime.Serialization.dll', '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll', '/reference:System.Security.dll', ('/reference:' + (Join-Path $wpf 'UIAutomationClient.dll')), ('/reference:' + (Join-Path $wpf 'UIAutomationTypes.dll')), ('/reference:' + (Join-Path $wpf 'WindowsBase.dll')), ('/reference:' + (Join-Path $wpf 'System.Speech.dll')), ('/reference:' + $winmd), ('/reference:' + (Join-Path $framework 'System.Runtime.dll')), ('/reference:' + (Join-Path $framework 'System.Runtime.WindowsRuntime.dll')), ('/reference:' + (Join-Path $framework 'System.Runtime.InteropServices.WindowsRuntime.dll')))
$resources = @(('/resource:' + $icon + ',Capri.Chip.ico'), ('/resource:' + (Join-Path $root 'assets\fonts\ShareTechMono-Regular.ttf') + ',Capri.Font.Mono.ttf'))
$resources += ('/resource:' + (Join-Path $root 'assets\cursor.png') + ',Capri.Cursor.png')
$resources += Get-ChildItem -LiteralPath (Join-Path $root 'assets\sprites') -Filter *.png | ForEach-Object { '/resource:' + $_.FullName + ',Capri.Sprite.' + $_.Name }
$resources += Get-ChildItem -LiteralPath (Join-Path $root 'assets\voice') -Filter *.wav | ForEach-Object { '/resource:' + $_.FullName + ',Capri.Voice.' + $_.Name }
$sources = @('Capri', 'Asides', 'Bolts', 'Commentary', 'Lookout', 'Brain', 'Talk', 'Mainframe', 'Neon', 'Notice', 'Engine', 'Ledges', 'Voice', 'Ears', 'Dictation', 'Agent', 'Hands', 'GameHud', 'Computer', 'Play', 'VoicePack', 'Startup', 'Presence', 'Player', 'Ocr', 'GameEars', 'GameNotes', 'GameKeys', 'Frames', 'Reminders', 'GameSense', 'GameWatch', 'SelfKnow', 'Care', 'Clips', 'Minis', 'Chess', 'ChessBoard', 'Files', 'FileFinder', 'Websites', 'Ink', 'Clones', 'FollowUps', 'WatchAlong', 'AssemblyInfo') | ForEach-Object { Join-Path $root ('app\' + $_ + '.cs') }
& $compiler ($shared + $references + $resources + @('/unsafe+', ('/out:' + $app)) + $sources)
if ($LASTEXITCODE -ne 0) { throw 'Capri compilation failed.' }
& $compiler ($shared + @(('/out:' + $setup), '/reference:Microsoft.CSharp.dll', '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll', ('/resource:' + $app + ',Capri.App.exe'), ('/resource:' + $icon + ',Capri.Chip.ico'), ('/resource:' + (Join-Path $root 'assets\chip.png') + ',Capri.Chip.png'), (Join-Path $root 'app\Setup.cs'), (Join-Path $root 'app\Neon.cs'), (Join-Path $root 'app\Notice.cs'), (Join-Path $root 'app\VoicePack.cs'), (Join-Path $root 'app\Startup.cs'), (Join-Path $root 'app\AssemblyInfo.cs')))
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Write-Output "Built $app and $setup"
if (-not $SmokeTest) { return }
# GitHub's Windows has a 1024 x 768 screen, and Windows won't make a window bigger than its screen, so her windows at 150% and 200% came out cut off and failed their layout checks.
if ($env:GITHUB_ACTIONS -eq 'true') {
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class CapriScreen {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Mode {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        public short Spec, Driver, Size, Extra;
        public int Fields, X, Y, Orientation, Fixed;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Form;
        public short LogPixels;
        public int Bits, Width, Height, Flags, Frequency, Icm, Intent, Media, Dither, Reserved1, Reserved2, PanWidth, PanHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern bool EnumDisplaySettings(string device, int index, ref Mode mode);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern int ChangeDisplaySettings(ref Mode mode, int flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    public static string Now { get { return GetSystemMetrics(0) + " x " + GetSystemMetrics(1); } }
    public static int Set(int width, int height) {
        Mode mode = new Mode();
        mode.Size = (short)Marshal.SizeOf(typeof(Mode));
        if (!EnumDisplaySettings(null, -1, ref mode)) return -100;
        mode.Width = width;
        mode.Height = height;
        mode.Fields = 0x80000 | 0x100000;
        return ChangeDisplaySettings(ref mode, 1);
    }
}
'@
    $was = [CapriScreen]::Now
    $answer = [CapriScreen]::Set(1920, 1080)
    Write-Output "Screen for the smoke test: $was, now $([CapriScreen]::Now) (Windows answered $answer)"
}
$runtime = Join-Path $out 'smoke\runtime'
$package = Join-Path $out 'smoke\package'
$check = Start-Process -FilePath $app -ArgumentList @('--smoke-test', ('"' + $runtime + '"')) -PassThru -WindowStyle Hidden
if (-not $check.WaitForExit($SmokeSeconds * 1000)) { $check.Kill(); throw 'Runtime smoke test timed out.' }
if (Test-Path -LiteralPath (Join-Path $runtime 'smoke-test.txt')) { Get-Content -LiteralPath (Join-Path $runtime 'smoke-test.txt') }
elseif ($check.ExitCode -eq 0) { throw 'Runtime smoke test wrote no results. Another smoke test probably held the lock; rerun it when that one ends.' }
if ($check.ExitCode -ne 0) {
    if (Test-Path -LiteralPath (Join-Path $runtime 'failure.txt')) { Get-Content -LiteralPath (Join-Path $runtime 'failure.txt') }
    throw 'Runtime smoke test failed.'
}
$verify = Start-Process -FilePath $setup -ArgumentList @('--verify', ('"' + $package + '"')) -PassThru -WindowStyle Hidden
if (-not $verify.WaitForExit($SmokeSeconds * 1000)) { $verify.Kill(); throw 'Installer check timed out.' }
if ($verify.ExitCode -ne 0) {
    if (Test-Path -LiteralPath (Join-Path $package 'failure.txt')) { Get-Content -LiteralPath (Join-Path $package 'failure.txt') }
    throw 'Installer check failed.'
}
if ((Get-FileHash -LiteralPath (Join-Path $package 'Capri.exe')).Hash -ne (Get-FileHash -LiteralPath $app).Hash) { throw 'Installer payload differs from the app executable.' }
Get-Content -LiteralPath (Join-Path $package 'packaging-test.txt')
Write-Output 'PASS installer payload matches the app'
