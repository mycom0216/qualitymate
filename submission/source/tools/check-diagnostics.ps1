$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$runtime=Join-Path $project 'submission/runtime'
$fixture=Join-Path $project ('tmp/diagnostics-'+[guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $runtime -Destination $fixture -Recurse
$exe=Join-Path $fixture 'QualityMate.exe'
$configFile=Join-Path $fixture 'appsettings.json'
$reportFile=Join-Path $fixture 'environment-check.json'
$config=Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
$results=@()
& $exe --diagnose | Out-Null
if($LASTEXITCODE -ne 0){throw 'Expected ready environment'}
$results+=@{case='complete runtime and installed AI';exit=0}
$config.Ollama.Model='qualitymate-nonexistent-test-model:never-installed'
$config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configFile -Encoding utf8
& $exe --diagnose | Out-Null
if($LASTEXITCODE -ne 2){throw 'Expected model warning'}
$r=Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json
if($r.readyForAI -or $r.warnings -ne 1){throw 'Incorrect model warning report'}
$results+=@{case='missing model detected without downloading';exit=2}
'{broken json' | Set-Content -LiteralPath $configFile -Encoding utf8
& $exe --diagnose | Out-Null
if($LASTEXITCODE -ne 1){throw 'Expected configuration failure'}
$results+=@{case='invalid configuration rejected';exit=1}
@{testedAt=(Get-Date -Format o);passed=$true;cases=$results} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $project 'submission/evidence/diagnostics-checks.json') -Encoding utf8
$results | ConvertTo-Json
