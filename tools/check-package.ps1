# =====================================================================
# check-package.ps1  -  제출용 자체 포함 실행 파일 스모크 검사
# =====================================================================
# 목적  : submission/runtime/QualityMate.exe(설치 없이 실행하는 배포본)를 임시 데이터로 실행해
#         시드 데이터, AI 연결, RAG 답변, CSV 가져오기, 화면 파일(CSS/JS) 제공이 모두 되는지 확인한다.
# 입력  : 없음. 포트 5191과 임시 데이터 폴더(tmp 아래)를 사용한다. Ollama와 모델이 준비돼 있어야 한다.
# 출력  : submission/evidence/package-smoke.json 저장 후 내용을 출력한다. 실패하면 예외로 중단.
# 실행  : PowerShell 7에서 ./tools/check-package.ps1
# =====================================================================
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
# 검사 대상: 소스가 아니라 배포용 runtime 폴더의 실행 파일.
$runtime=Join-Path $project 'submission/runtime'
$testData=Join-Path $project ('tmp/package-smoke-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testData -Force | Out-Null
# 배포본을 임시 데이터 폴더로 실행한다(실제 데이터와 분리).
$server=Start-Process -FilePath (Join-Path $runtime 'QualityMate.exe') -ArgumentList @('--urls','http://127.0.0.1:5191','--DataDirectory',$testData) -WorkingDirectory $runtime -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testData 'server.log') -RedirectStandardError (Join-Path $testData 'error.log')
try{
    # 서버가 뜰 때까지 대기(0.25초 x 최대 40회) 후 시드 제품 3개 확인.
    $products=$null
    for($i=0;$i -lt 40;$i++){try{$products=Invoke-RestMethod 'http://127.0.0.1:5191/api/inspections';break}catch{Start-Sleep -Milliseconds 250}}
    if($products.Count -ne 3){throw 'Packaged app seed check failed'}
    # AI(Ollama 모델) 준비 상태 확인.
    $status=Invoke-RestMethod 'http://127.0.0.1:5191/api/status'
    if(-not $status.aiReady){throw 'Packaged app AI check failed'}
    $body=@{productId='QM-001';question='재검사에 필요한 기록은 무엇인가요?'}|ConvertTo-Json
    # RAG 질문 1건: mode가 llm이고 답변에 '검사자'(재검사 기록 항목)가 있어야 한다.
    $chat=Invoke-RestMethod 'http://127.0.0.1:5191/api/chat' -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    if($chat.mode -ne 'llm' -or -not $chat.answer.Contains('검사자')){throw 'Packaged app RAG check failed'}
    $body=@{csv=(Get-Content (Join-Path $project 'samples/VisionQC-demo.csv') -Raw)}|ConvertTo-Json
    # 샘플 CSV(samples/VisionQC-demo.csv) 가져오기: 3건 추가되어야 한다.
    $import=Invoke-RestMethod 'http://127.0.0.1:5191/api/import/visionqc' -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    if($import.added -ne 3){throw 'Packaged app CSV check failed'}
    # 화면과 CSS/JS 파일이 배포본에 포함되어 제공되는지 확인.
    $page=Invoke-WebRequest 'http://127.0.0.1:5191/'
    if($page.Content -notmatch 'QualityMate'){throw 'Packaged app UI check failed'}
    if($page.Content -notmatch '/code-theme.css'){throw 'Packaged code theme reference missing'}
    $theme=Invoke-WebRequest 'http://127.0.0.1:5191/code-theme.css'
    if($theme.Content -notmatch 'code-backdrop'){throw 'Packaged code theme asset missing'}
    foreach($asset in @('polish.css','polish.js','rose.css')) {
        $assetResult=Invoke-WebRequest ('http://127.0.0.1:5191/'+$asset)
        if($assetResult.StatusCode -ne 200 -or $page.Content -notmatch $asset){throw ('Packaged polish asset missing: '+$asset)}
    }
    @{passed=$true;seedProducts=$products.Count;aiReady=$status.aiReady;model=$status.model;mode=$chat.mode;imported=$import.added;answer=$chat.answer;testedAt=(Get-Date -Format o);scope='개발 PC에서 자체 포함 EXE를 별도 포트와 데이터로 실행. 다른 PC 검증은 별도.'}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $project 'submission/evidence/package-smoke.json') -Encoding utf8
    Get-Content (Join-Path $project 'submission/evidence/package-smoke.json')
# 정리: 시험 서버 종료.
}finally{if(-not $server.HasExited){$server.Kill($true)}}

