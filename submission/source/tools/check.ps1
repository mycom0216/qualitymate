param([string]$BaseUrl='http://127.0.0.1:5188')
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$testData=Join-Path $project ('tmp/check-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testData -Force | Out-Null
$server=Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-build','--urls',$BaseUrl,'--DataDirectory',$testData) -WorkingDirectory $project -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testData 'server.log') -RedirectStandardError (Join-Path $testData 'server-error.log')
function Call($method,$route,$body=$null) {
    $params=@{Uri="$BaseUrl$route";Method=$method;SkipHttpErrorCheck=$true}
    if($null -ne $body){$params.ContentType='application/json; charset=utf-8';$params.Body=[Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Depth 8))}
    $r=Invoke-WebRequest @params
    return @{Status=[int]$r.StatusCode;Data=($r.Content|ConvertFrom-Json)}
}
function Check($condition,$name){if(-not $condition){throw "FAIL $name"}; "PASS $name"}
try {
    $ready=$false
    for($i=0;$i -lt 40;$i++){
        try { $initial=Call GET '/api/inspections';$ready=$true;break } catch {Start-Sleep -Milliseconds 250}
    }
    if(-not $ready){throw 'Test server failed to start'}
    Check ($initial.Data.Count -eq 3) 'seed products'
    $request=@{result='PASS';operator='검증용 검사자';note='시연용 재검사';expectedRevision=0;requestId=[guid]::NewGuid().ToString()}
    $r=Call POST '/api/inspections/QM-001/reinspections' $request
    Check ($r.Status -eq 200 -and $r.Data.revision -eq 1 -and $r.Data.initialResult -eq 'FAIL' -and $r.Data.currentResult -eq 'PASS') 'preserve original and append'
    $r=Call POST '/api/inspections/QM-001/reinspections' $request
    Check ($r.Status -eq 200 -and $r.Data.revision -eq 1) 'idempotent retry'
    $request.requestId=[guid]::NewGuid().ToString()
    $r=Call POST '/api/inspections/QM-001/reinspections' $request
    Check ($r.Status -eq 409) 'stale revision rejected'
    $request.operator=' '
    $r=Call POST '/api/inspections/QM-001/reinspections' $request
    Check ($r.Status -eq 400) 'invalid operator rejected'
    $r=Call POST '/api/chat' @{productId='MISSING';question='재검사 절차'}
    Check ($r.Status -eq 404) 'unknown product rejected'
    $r=Call POST '/api/chat' @{productId='QM-001';question=''}
    Check ($r.Status -eq 400) 'empty question rejected'
    $r=Call POST '/api/chat' @{productId='QM-001';question='내일 날씨'}
    Check ($r.Data.mode -eq 'no-evidence' -and $r.Data.sources.Count -eq 0) 'unsupported question'
    $r=Call POST '/api/chat' @{productId='QM-003';question='이물 의심 제품은 무엇을 확인하나요?'}
    Check ($r.Data.mode -eq 'llm' -and $r.Data.sources.id -contains 'SOP-02' -and $r.Data.answer.Length -gt 0) 'live LLM and relevant evidence'
    $saved=Call GET '/api/inspections'
    Check (($saved.Data | Where-Object id -eq 'QM-001').revision -eq 1) 'saved history'
    Check (Test-Path (Join-Path $testData 'qualitymate.db')) 'SQLite database created'
    $csv=@'
Id,JobId,InspectedAtUtc,OriginalVerdict,DefectScore,ElapsedMs,ImagePath,ErrorMessage
1,2,2026-11-19T01:00:00Z,NG,0.75,21,test.png,
2,2,2026-11-19T01:01:00Z,ERROR,,10,broken.png,Read failed
'@
    $r=Call POST '/api/import/visionqc' @{csv=$csv}
    Check ($r.Status -eq 200 -and $r.Data.added -eq 2) 'VisionQC NG and ERROR import'
    $r=Call POST '/api/import/visionqc' @{csv=$csv}
    Check ($r.Status -eq 200 -and $r.Data.skipped -eq 2) 'duplicate CSV idempotent'
    $r=Call POST '/api/import/visionqc' @{csv=$csv.Replace('0.75','0.80')}
    Check ($r.Status -eq 409) 'conflicting source rejected'
    $r=Call POST '/api/import/visionqc' @{csv=$csv.Replace('1,2,2026','3,2,2026').Replace('ERROR','BOGUS')}
    Check ($r.Status -eq 400) 'invalid CSV rejected'
    $r=Call GET '/api/inspections'
    Check ($r.Data.Count -eq 5 -and ($r.Data | Where-Object id -eq 'VQC-2-2').initialResult -eq 'ERROR') 'atomic import and ERROR preserved'
    $server.Kill($true);$server.WaitForExit()
    $server=Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-build','--urls',$BaseUrl,'--DataDirectory',$testData) -WorkingDirectory $project -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testData 'restart.log') -RedirectStandardError (Join-Path $testData 'restart-error.log')
    $restored=$null
    for($i=0;$i -lt 40;$i++){
        try{$restored=Call GET '/api/inspections';break}catch{Start-Sleep -Milliseconds 250}
    }
    Check ($restored.Data.Count -eq 5 -and ($restored.Data | Where-Object id -eq 'QM-001').revision -eq 1) 'restart preserves SQLite history'

} finally {
    if(-not $server.HasExited){$server.Kill($true)}
}

