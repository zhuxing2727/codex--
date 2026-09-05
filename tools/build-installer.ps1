[CmdletBinding()]
param(
  [switch]$SkipRuntimeDownload
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $project 'dist'
$stage = Join-Path $dist 'stage'
$payload = Join-Path $dist 'ErgouziWhaleWidget-payload.zip'
$portable = Join-Path $dist 'ErgouziWhaleWidget-Portable.zip'
$setup = Join-Path $dist 'ErgouziWhaleWidget-Setup.exe'

Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $payload,$portable,$setup -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage | Out-Null

$excluded = @('.git','.catpaw','_rollback_verify','dist','node_modules','runtime','tools/__pycache__')
Get-ChildItem -LiteralPath $project -Force -Recurse | ForEach-Object {
  $relative = $_.FullName.Substring($project.Length).TrimStart('\','/')
  if ($excluded | Where-Object { $relative -eq $_ -or $relative.StartsWith($_ + '\') }) { return }
  if ($_.Name -match '\.(baseline|log)$' -or $_.Name -in @('DIFF_FILE','VERIFICATION.txt','ROLLBACK.sh')) { return }
  $destination = Join-Path $stage $relative
  if ($_.PSIsContainer) { New-Item -ItemType Directory -Force -Path $destination | Out-Null }
  else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
  }
}

$runtimeTarget = Join-Path $stage 'runtime'
New-Item -ItemType Directory -Force -Path $runtimeTarget | Out-Null
$localNode = Join-Path $project 'runtime\node.exe'
if (Test-Path -LiteralPath $localNode) {
  Copy-Item -LiteralPath $localNode -Destination (Join-Path $runtimeTarget 'node.exe') -Force
} elseif (-not $SkipRuntimeDownload) {
  $nodeVersion = 'v22.14.0'
  $nodeZip = Join-Path $env:TEMP ("node-$nodeVersion-win-x64.zip")
  if (-not (Test-Path -LiteralPath $nodeZip)) {
    Invoke-WebRequest -Uri "https://nodejs.org/dist/$nodeVersion/node-$nodeVersion-win-x64.zip" -OutFile $nodeZip
  }
  $nodeExtract = Join-Path $env:TEMP ("ergouzi-node-$nodeVersion")
  Remove-Item -LiteralPath $nodeExtract -Recurse -Force -ErrorAction SilentlyContinue
  Expand-Archive -LiteralPath $nodeZip -DestinationPath $env:TEMP -Force
  Rename-Item -LiteralPath (Join-Path $env:TEMP "node-$nodeVersion-win-x64") -NewName (Split-Path -Leaf $nodeExtract)
  Copy-Item -LiteralPath (Join-Path $nodeExtract 'node.exe') -Destination (Join-Path $runtimeTarget 'node.exe') -Force
} else {
  $node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
  if (-not $node) { throw 'Node.js not found. Omit -SkipRuntimeDownload or install Node.js first.' }
  Copy-Item -LiteralPath $node -Destination (Join-Path $runtimeTarget 'node.exe') -Force
}

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $portable -CompressionLevel Optimal
Copy-Item -LiteralPath $portable -Destination $payload -Force

$bootstrapSource = Join-Path $PSScriptRoot 'InstallerBootstrap.cs'
$bootstrapExe = Join-Path $dist 'InstallerBootstrap.exe'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path ($framework -replace 'Framework64','Framework') 'csc.exe' }
if (-not (Test-Path -LiteralPath $csc)) { throw 'csc.exe was not found. Install .NET Framework 4.x Developer Tools.' }
& $csc /nologo /target:exe /optimize+ /out:$bootstrapExe /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll $bootstrapSource
if ($LASTEXITCODE -ne 0) { throw 'Installer bootstrap compilation failed.' }

$marker = [Text.Encoding]::ASCII.GetBytes("ERGOUZI_PAYLOAD_START`n")
$out = [IO.File]::Open($setup, [IO.FileMode]::Create, [IO.FileAccess]::Write)
try {
  $base = [IO.File]::OpenRead($bootstrapExe)
  try { $base.CopyTo($out) } finally { $base.Dispose() }
  $out.Write($marker, 0, $marker.Length)
  $zip = [IO.File]::OpenRead($payload)
  try { $zip.CopyTo($out) } finally { $zip.Dispose() }
} finally { $out.Dispose() }

Remove-Item -LiteralPath $stage,$payload,$bootstrapExe -Recurse -Force -ErrorAction SilentlyContinue
Write-Output "Created: $setup"
Write-Output "Created: $portable"
