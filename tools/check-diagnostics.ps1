# =====================================================================
# check-diagnostics.ps1  -  오프라인 진단(--diagnose) 종료 코드 검증
# =====================================================================
# 목적  : 배포본의 진단 기능이 상황별로 올바른 종료 코드를 내는지 확인한다.
#           정상(AI 모델 설치)  -> 0 준비 완료
#           모델이 설치되지 않음 -> 2 AI 준비 필요 (경고)
#           설정 파일이 깨짐     -> 1 앱 환경 오류
# 입력  : 없음. submission/runtime을 tmp 아래로 복사해 그 복사본만 수정하므로 원본 배포본은 바뀌지 않는다.
# 출력  : submission/evidence/diagnostics-checks.json 저장 + 사례별 결과 JSON 출력. 기대와 다르면 예외로 중단.
# 실행  : PowerShell 7에서 ./tools/check-diagnostics.ps1  (Ollama 준비 필요: 첫 사례가 정상 환경을 기대)
# =====================================================================
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$runtime=Join-Path $project 'submission/runtime'
# 원본을 건드리지 않도록 복사본 폴더(fixture)에서 시험한다.
$fixture=Join-Path $project ('tmp/diagnostics-'+[guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $runtime -Destination $fixture -Recurse
$exe=Join-Path $fixture 'QualityMate.exe'
$configFile=Join-Path $fixture 'appsettings.json'
$reportFile=Join-Path $fixture 'environment-check.json'
$config=Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
$results=@()
# 사례 1: 완전한 배포본 + 설치된 모델 -> 종료 코드 0이어야 한다.
& $exe --diagnose | Out-Null
if($LASTEXITCODE -ne 0){throw 'Expected ready environment'}
$results+=@{case='complete runtime and installed AI';exit=0}
# 사례 2: 설정의 모델 이름을 존재하지 않는 값으로 바꾼다. 진단은 모델을 내려받지 않고 경고만 하므로 종료 코드 2, 보고서의 warnings가 1이어야 한다.
$config.Ollama.Model='qualitymate-nonexistent-test-model:never-installed'
$config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configFile -Encoding utf8
& $exe --diagnose | Out-Null
if($LASTEXITCODE -ne 2){throw 'Expected model warning'}
$r=Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json
if($r.readyForAI -or $r.warnings -ne 1){throw 'Incorrect model warning report'}
$results+=@{case='missing model detected without downloading';exit=2}
# 사례 3: appsettings.json을 깨뜨린다 -> 설정 오류이므로 종료 코드 1.
'{broken json' | Set-Content -LiteralPath $configFile -Encoding utf8
& $exe --diagnose | Out-Null
if($LASTEXITCODE -ne 1){throw 'Expected configuration failure'}
$results+=@{case='invalid configuration rejected';exit=1}
# 세 사례를 모두 통과했을 때만 증거 파일을 기록한다.
@{testedAt=(Get-Date -Format o);passed=$true;cases=$results} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $project 'submission/evidence/diagnostics-checks.json') -Encoding utf8
$results | ConvertTo-Json
