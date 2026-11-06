<#
.SYNOPSIS
  Rebuild and (re)start your MultiDir in one step: quits the running one first (it saves its session),
  builds, then starts the new build detached so this terminal is free again.

.EXAMPLE
  ./tools/run.ps1            # quit running MultiDir → build → start
  ./tools/run.ps1 -NoStart   # only quit + build
#>
param([switch]$NoStart)

$project = Join-Path $PSScriptRoot "..\src\MultiDir\MultiDir.csproj"
$exe = Join-Path $PSScriptRoot "..\src\MultiDir\bin\Debug\net8.0-windows\MultiDir.exe"

# 1) Quit the running MultiDir (never the off-screen automation test instances).
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'MultiDir.exe'" | Where-Object { $_.CommandLine -notlike "*--automation*" })
if ($running.Count -gt 0) {
  # Builds that know the "::exit" pipe message quit gracefully (session saved, like tray → 結束).
  # Older builds don't: those are stopped instead (their last 30 s autosave is kept).
  $dll = Join-Path (Split-Path $running[0].ExecutablePath) "MultiDir.dll"
  $graceful = $false
  if (Test-Path $dll) {
    $marker = [System.Text.Encoding]::Unicode.GetBytes("::exit")
    $bytes = [System.IO.File]::ReadAllBytes($dll)
    $graceful = ([System.BitConverter]::ToString($bytes).Contains([System.BitConverter]::ToString($marker)))
  }
  if ($graceful) {
    try {
      $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "MultiDir.Instance.$env:USERNAME", [System.IO.Pipes.PipeDirection]::Out)
      $pipe.Connect(2000)
      $w = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
      $w.WriteLine("::exit"); $w.Dispose(); $pipe.Dispose()
      "asked MultiDir to quit…"
    } catch { $graceful = $false }
  }
  foreach ($p in $running) {
    $proc = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
    if ($proc -and -not ($graceful -and $proc.WaitForExit(8000))) {
      Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
      "stopped MultiDir (pid $($p.ProcessId))"
    }
  }
}

# 2) Build.
dotnet build $project -v q -nologo
if ($LASTEXITCODE -ne 0) { "BUILD FAILED"; exit 1 }

# 3) Start the new build, detached.
if (-not $NoStart) {
  Start-Process -FilePath (Resolve-Path $exe)
  "MultiDir started"
}
