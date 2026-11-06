<#
.SYNOPSIS
  Drives a MultiDir test instance over its automation pipe without touching the real mouse/keyboard.

.EXAMPLE
  ./tools/mdctl.ps1 build                    # build the test copy into artifacts/automation
  ./tools/mdctl.ps1 start                    # launch an off-screen automation instance
  ./tools/mdctl.ps1 layout
  ./tools/mdctl.ps1 shot C:\temp\a.png
  ./tools/mdctl.ps1 click 120 80
  ./tools/mdctl.ps1 "tab float 1" "wait 300" "layout"   # several commands in one session
  ./tools/mdctl.ps1 stop
#>
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Commands)

# Test builds get their own bin AND obj via --artifacts-path, so they never fight with (or require closing)
# the user's running MultiDir.
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\artifacts\automation"))
$exe = Join-Path $artifacts "bin\MultiDir\debug\MultiDir.exe"
$pipeName = "MultiDir.Automation"

function Find-Instance {
  Get-CimInstance Win32_Process -Filter "Name = 'MultiDir.exe'" |
    Where-Object { $_.CommandLine -like "*--automation*" }
}

if (-not $Commands -or $Commands.Count -eq 0) { $Commands = @("help") }

switch ($Commands[0]) {
  "build" {
    Find-Instance | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Start-Sleep -Milliseconds 500
    $out = dotnet build (Join-Path $PSScriptRoot "..\src\MultiDir\MultiDir.csproj") -v q -nologo --artifacts-path $artifacts 2>&1
    $ok = $LASTEXITCODE -eq 0
    $out | Select-String -Pattern ": error |: warning CS" | Select-Object -Unique | ForEach-Object { $_.Line }
    if ($ok) { "build OK -> $exe" } else { "BUILD FAILED" }
    return
  }
  "start" {
    Find-Instance | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    $extra = @($Commands | Select-Object -Skip 1)
    # Each test starts from the default layout unless "start keep" asks to restore the saved test session.
    if ($extra.Count -gt 0 -and $extra[0] -eq "keep") { $extra = @($extra | Select-Object -Skip 1) }
    else { Remove-Item (Join-Path $env:TEMP "MultiDir-automation\layout.xml"), (Join-Path $env:TEMP "MultiDir-automation\session.json"), (Join-Path $env:TEMP "MultiDir-automation\bookmarks.json"), (Join-Path $env:TEMP "MultiDir-automation\history.json") -ErrorAction SilentlyContinue }
    $argList = @("--automation") + ($extra | ForEach-Object { "`"$_`"" })
    # Never launch an incomplete build: a missing runtimeconfig makes Windows pop a ".NET runtime" dialog on the user's screen.
    $cfg = [System.IO.Path]::ChangeExtension($exe, ".runtimeconfig.json")
    if (-not (Test-Path $exe) -or -not (Test-Path $cfg)) { "ERROR test build missing or incomplete (run: mdctl.ps1 build)"; return }
    Start-Process -FilePath (Resolve-Path $exe) -ArgumentList $argList | Out-Null
    for ($i = 0; $i -lt 50; $i++) {
      Start-Sleep -Milliseconds 200
      $c = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
      try { $c.Connect(100); $c.Dispose(); "started"; return } catch { $c.Dispose() }
    }
    "ERROR automation pipe did not come up"; return
  }
  "stop" {
    Find-Instance | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; "stopped $($_.ProcessId)" }
    return
  }
}

$client = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
try { $client.Connect(3000) } catch { "ERROR no automation instance (run: mdctl.ps1 start)"; return }
$utf8 = New-Object System.Text.UTF8Encoding($false)
$writer = New-Object System.IO.StreamWriter($client, $utf8); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client, $utf8)
foreach ($cmd in $Commands) {
  "> $cmd"
  $writer.WriteLine($cmd)
  while (($line = $reader.ReadLine()) -ne $null -and $line -ne "<<END>>") { $line }
}
$client.Dispose()
