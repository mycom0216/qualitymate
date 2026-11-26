# =====================================================================
# check.ps1  -  기능 통합 검사 (17개 항목)
# =====================================================================
# 목적  : 개발 중인 서버를 별도 임시 DB로 띄워 조회, 재검사 저장, 멱등성, 버전 충돌, 입력 검증,
#         표준서 질의, VisionQC 가져오기, 재시작 후 데이터 보존을 HTTP로 확인한다.
# 입력  : -BaseUrl (기본 http://127.0.0.1:5188). 실제 시연 DB와 분리하기 위해 tmp 아래 임시 폴더를 만든다.
# 출력  : 항목마다 'PASS 이름' 한 줄을 출력한다. 하나라도 실패하면 'FAIL 이름' 예외로 즉시 중단한다.
# 실행  : PowerShell 7에서 ./tools/check.ps1  (사전: dotnet build 완료, Ollama와 모델 준비 - 'live LLM' 항목이 실제 모델을 호출)
# =====================================================================
param([string]$BaseUrl='http://127.0.0.1:5188')
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
# 임시 데이터 폴더: 실행마다 GUID로 새로 만들어 시연 데이터와 완전히 분리한다(--DataDirectory로 서버에 전달).
$testData=Join-Path $project ('tmp/check-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testData -Force | Out-Null
# 서버를 숨김 창으로 실행한다(--no-build: 이미 빌드된 결과 사용). 출력은 임시 폴더의 로그 파일로 보낸다.
$server=Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-build','--urls',$BaseUrl,'--DataDirectory',$testData) -WorkingDirectory $project -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testData 'server.log') -RedirectStandardError (Join-Path $testData 'server-error.log')
# HTTP 호출 도우미. 오류 상태 코드(400/404/409)도 예외 없이 받아 Status와 JSON 본문을 함께 돌려준다.
function Call($method,$route,$body=$null) {
    $params=@{Uri="$BaseUrl$route";Method=$method;SkipHttpErrorCheck=$true}
    if($null -ne $body){$params.ContentType='application/json; charset=utf-8';$params.Body=[Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Depth 8))}
    $r=Invoke-WebRequest @params
    return @{Status=[int]$r.StatusCode;Data=($r.Content|ConvertFrom-Json)}
}
# 검사 도우미: 조건이 거짓이면 예외로 중단, 참이면 PASS 출력.
function Check($condition,$name){if(-not $condition){throw "FAIL $name"}; "PASS $name"}
try {
    $ready=$false
    # 서버 기동 대기: 0.25초 간격으로 최대 40번(약 10초) 시도한다.
    for($i=0;$i -lt 40;$i++){
        try { $initial=Call GET '/api/inspections';$ready=$true;break } catch {Start-Sleep -Milliseconds 250}
    }
    if(-not $ready){throw 'Test server failed to start'}
    # 초기 데이터: 시연용 제품 3개가 들어 있어야 한다.
    Check ($initial.Data.Count -eq 3) 'seed products'
    # 재검사 저장 시험용 요청(요청 ID는 새 GUID, expectedRevision 0).
    $request=@{result='PASS';operator='검증용 검사자';note='시연용 재검사';expectedRevision=0;requestId=[guid]::NewGuid().ToString()}
    $r=Call POST '/api/inspections/QM-001/reinspections' $request
    # 저장 후에도 최초 판정(FAIL)은 보존되고 최신 판정만 PASS로 바뀌는지.
    Check ($r.Status -eq 200 -and $r.Data.revision -eq 1 -and $r.Data.initialResult -eq 'FAIL' -and $r.Data.currentResult -eq 'PASS') 'preserve original and append'
    $r=Call POST '/api/inspections/QM-001/reinspections' $request
    # 같은 요청 ID로 재전송하면 중복 저장 없이 revision 1 그대로 200(멱등성).
    Check ($r.Status -eq 200 -and $r.Data.revision -eq 1) 'idempotent retry'
    $request.requestId=[guid]::NewGuid().ToString()
    $r=Call POST '/api/inspections/QM-001/reinspections' $request
    # 새 요청 ID지만 expectedRevision이 오래됨(0, 현재 1) -> 409 버전 충돌.
    Check ($r.Status -eq 409) 'stale revision rejected'
    $request.operator=' '
    $r=Call POST '/api/inspections/QM-001/reinspections' $request
    # 검사자가 공백이면 400 입력 오류.
    Check ($r.Status -eq 400) 'invalid operator rejected'
    $r=Call POST '/api/chat' @{productId='MISSING';question='재검사 절차'}
    # 없는 제품으로 질문하면 404.
    Check ($r.Status -eq 404) 'unknown product rejected'
    $r=Call POST '/api/chat' @{productId='QM-001';question=''}
    # 빈 질문은 400.
    Check ($r.Status -eq 400) 'empty question rejected'
    $r=Call POST '/api/chat' @{productId='QM-001';question='내일 날씨'}
    # 표준서 범위 밖 질문은 모델 호출 없이 no-evidence, 인용 절 0개.
    Check ($r.Data.mode -eq 'no-evidence' -and $r.Data.sources.Count -eq 0) 'unsupported question'
    $r=Call POST '/api/chat' @{productId='QM-003';question='이물 의심 제품은 무엇을 확인하나요?'}
    # 실제 LLM 호출 경로: mode가 llm이고 이물 관련 SOP-02가 근거로 포함되는지.
    Check ($r.Data.mode -eq 'llm' -and $r.Data.sources.id -contains 'SOP-02' -and $r.Data.answer.Length -gt 0) 'live LLM and relevant evidence'
    $saved=Call GET '/api/inspections'
    # 저장한 재검사 이력이 다시 조회해도 남아 있는지.
    Check (($saved.Data | Where-Object id -eq 'QM-001').revision -eq 1) 'saved history'
    Check (Test-Path (Join-Path $testData 'qualitymate.db')) 'SQLite database created'
    # VisionQC CSV 시험 데이터: NG 1행, ERROR 1행(점수 비어 있음).
    $csv=@'
Id,JobId,InspectedAtUtc,OriginalVerdict,DefectScore,ElapsedMs,ImagePath,ErrorMessage
1,2,2026-11-19T01:00:00Z,NG,0.75,21,test.png,
2,2,2026-11-19T01:01:00Z,ERROR,,10,broken.png,Read failed
'@
    $r=Call POST '/api/import/visionqc' @{csv=$csv}
    # NG와 ERROR가 모두 추가되는지(ERROR는 불합격으로 바뀌지 않고 유지).
    Check ($r.Status -eq 200 -and $r.Data.added -eq 2) 'VisionQC NG and ERROR import'
    $r=Call POST '/api/import/visionqc' @{csv=$csv}
    # 같은 CSV를 다시 올리면 2건 모두 중복 제외(skipped).
    Check ($r.Status -eq 200 -and $r.Data.skipped -eq 2) 'duplicate CSV idempotent'
    $r=Call POST '/api/import/visionqc' @{csv=$csv.Replace('0.75','0.80')}
    # 같은 ID인데 점수가 다른 CSV -> 409 충돌. 전체 취소.
    Check ($r.Status -eq 409) 'conflicting source rejected'
    $r=Call POST '/api/import/visionqc' @{csv=$csv.Replace('1,2,2026','3,2,2026').Replace('ERROR','BOGUS')}
    # 허용되지 않는 판정값(BOGUS)이 섞인 CSV는 400.
    Check ($r.Status -eq 400) 'invalid CSV rejected'
    $r=Call GET '/api/inspections'
    # 앞선 충돌/오류 가져오기가 DB를 바꾸지 않았는지(원자성): 총 5건, ERROR 판정 유지.
    Check ($r.Data.Count -eq 5 -and ($r.Data | Where-Object id -eq 'VQC-2-2').initialResult -eq 'ERROR') 'atomic import and ERROR preserved'
    # 서버를 강제 종료하고 같은 데이터 폴더로 다시 띄워 재시작 후에도 데이터가 남는지 확인한다.
    $server.Kill($true);$server.WaitForExit()
    $server=Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-build','--urls',$BaseUrl,'--DataDirectory',$testData) -WorkingDirectory $project -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testData 'restart.log') -RedirectStandardError (Join-Path $testData 'restart-error.log')
    $restored=$null
    for($i=0;$i -lt 40;$i++){
        try{$restored=Call GET '/api/inspections';break}catch{Start-Sleep -Milliseconds 250}
    }
    Check ($restored.Data.Count -eq 5 -and ($restored.Data | Where-Object id -eq 'QM-001').revision -eq 1) 'restart preserves SQLite history'

# 성공/실패와 관계없이 시험 서버 프로세스를 정리한다.
} finally {
    if(-not $server.HasExited){$server.Kill($true)}
}

