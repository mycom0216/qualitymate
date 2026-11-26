$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$runtime=Join-Path $project 'submission/runtime'
$testData=Join-Path $project ('tmp/package-smoke-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testData -Force | Out-Null
$server=Start-Process -FilePath (Join-Path $runtime 'QualityMate.exe') -ArgumentList @('--urls','http://127.0.0.1:5191','--DataDirectory',$testData) -WorkingDirectory $runtime -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testData 'server.log') -RedirectStandardError (Join-Path $testData 'error.log')
try{
    $products=$null
    for($i=0;$i -lt 40;$i++){try{$products=Invoke-RestMethod 'http://127.0.0.1:5191/api/inspections';break}catch{Start-Sleep -Milliseconds 250}}
    if($products.Count -ne 3){throw 'Packaged app seed check failed'}
    $status=Invoke-RestMethod 'http://127.0.0.1:5191/api/status'
    if(-not $status.aiReady){throw 'Packaged app AI check failed'}
    $body=@{productId='QM-001';question='재검사에 필요한 기록은 무엇인가요?'}|ConvertTo-Json
    $chat=Invoke-RestMethod 'http://127.0.0.1:5191/api/chat' -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    if($chat.mode -ne 'llm' -or -not $chat.answer.Contains('검사자')){throw 'Packaged app RAG check failed'}
    $body=@{csv=(Get-Content (Join-Path $project 'samples/VisionQC-demo.csv') -Raw)}|ConvertTo-Json
    $import=Invoke-RestMethod 'http://127.0.0.1:5191/api/import/visionqc' -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    if($import.added -ne 3){throw 'Packaged app CSV check failed'}
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
}finally{if(-not $server.HasExited){$server.Kill($true)}}

