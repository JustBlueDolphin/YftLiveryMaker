# Builds YFT Livery Maker and puts the two single-file programs, and a release zip, in .\dist
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# CodeWalker comes from its official repository as a git submodule.
if (-not (Test-Path "$root\third_party\CodeWalker\CodeWalker.Core\CodeWalker.Core.csproj")) {
    git -C $root submodule update --init --depth 1
    if ($LASTEXITCODE -ne 0) { throw 'Could not fetch CodeWalker. Clone with --recursive, or run: git submodule update --init' }
}

# Any .NET SDK 6+ works (it builds for .NET Framework 4.8, which ships with Windows 10/11).
$dotnet = $null
foreach ($candidate in @("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe", 'dotnet')) {
    $exe = Get-Command $candidate -ErrorAction SilentlyContinue
    if ($exe -and (& $exe.Source --list-sdks 2>$null)) { $dotnet = $exe.Source; break }
}
if (-not $dotnet) { throw 'No .NET SDK found. Install one from https://dot.net (any version 6 or newer).' }

& $dotnet build "$root\src\YftLiveryMaker\YftLiveryMaker.csproj" -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'GUI build failed' }
& $dotnet build "$root\src\LiveryCli\LiveryCli.csproj" -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }

& "$root\src\LiveryCli\bin\Release\net48\yftlivery.exe" selftest
if ($LASTEXITCODE -ne 0) { throw 'self-tests failed' }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# Clear out copies parked by earlier updates (fails harmlessly if one is still running).
Get-ChildItem $dist -Filter '*.old-*.exe' -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue

function Install-Exe($source, $name) {
    $target = Join-Path $dist $name
    if (Test-Path $target) {
        try { [IO.File]::Open($target, 'Open', 'ReadWrite', 'None').Dispose() }
        catch {
            # The app is open. Windows lets a running .exe be renamed, so park it and drop the new one in;
            # the next launch is the new version, and it deletes the parked copy.
            $parked = [IO.Path]::GetFileNameWithoutExtension($name) + '.old-' + (Get-Date -Format 'yyyyMMddHHmmss') + '.exe'
            Rename-Item $target $parked
            Write-Host "$name was running: parked it as $parked"
        }
    }
    Copy-Item $source $target -Force
}

Install-Exe "$root\src\YftLiveryMaker\bin\Release\net48\YftLiveryMaker.exe" 'YftLiveryMaker.exe'
Install-Exe "$root\src\LiveryCli\bin\Release\net48\yftlivery.exe" 'yftlivery.exe'
if (Get-Command python -ErrorAction SilentlyContinue) {
    foreach ($server in @('yftlivery.exe mcp', 'YftLiveryMaker.exe --mcp')) {
        $serverArgs = $server -split ' '
        python -I "$root\tests\mcp_smoke.py" (Join-Path $dist $serverArgs[0]) $serverArgs[1]
        if ($LASTEXITCODE -ne 0) { throw "MCP smoke test failed: $server" }
    }
} else { Write-Host 'Python not found: MCP smoke test skipped' }

Copy-Item "$root\docs\README.txt" $dist -Force
Copy-Item "$root\THIRD-PARTY-NOTICES.txt" $dist -Force
Copy-Item "$root\LICENSE" "$dist\LICENSE.txt" -Force

# The zip that goes on the GitHub release: both programs with the guide and the licences.
$version = (Select-Xml -Path "$root\src\YftLiveryMaker\YftLiveryMaker.csproj" -XPath '//Version').Node.InnerText
$zip = Join-Path $dist "YftLiveryMaker-v$version.zip"
$files = 'YftLiveryMaker.exe', 'yftlivery.exe', 'README.txt', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt' | ForEach-Object { Join-Path $dist $_ }
Compress-Archive -Path $files -DestinationPath $zip -Force

Get-ChildItem $dist | Format-Table Name, Length -AutoSize
Write-Host "Done: $dist"
