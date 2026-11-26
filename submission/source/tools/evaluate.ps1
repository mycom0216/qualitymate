param([string]$BaseUrl='http://127.0.0.1:5187')
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$cases=Get-Content (Join-Path $PSScriptRoot 'questions.json') -Raw | ConvertFrom-Json
$results=@()
foreach($case in $cases){
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $body=@{productId=$case.productId;question=$case.question}|ConvertTo-Json
    $r=Invoke-RestMethod "$BaseUrl/api/chat" -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    $timer.Stop()
    $retrieval=if($case.expectedSource){$r.sources.id -contains $case.expectedSource}else{$r.mode -eq 'no-evidence'}
    $mode=if($case.expectedSource){$r.mode -eq 'llm'}else{$r.mode -eq 'no-evidence'}
    $cited=if($case.expectedSource){$r.answer -match '\[SOP-\d+\]'}else{$true}
    $grounded=$true
    if($r.mode -eq 'llm'){
        foreach($line in ($r.answer -split "\n" | Where-Object { $_.Trim() })){
            if($line -notmatch '^(.*) \[(SOP-\d+)\]$'){$grounded=$false;continue}
            $text=$Matches[1];$id=$Matches[2]
            $source=$r.sources | Where-Object id -eq $id
            if(-not $source -or -not $source.text.Contains($text)){$grounded=$false}
        }
    }
    $answerCoverage=if($case.expectedText){$r.answer.Contains($case.expectedText)}else{$true}
    $pass=$retrieval -and $mode -and $cited -and $grounded -and $answerCoverage
    $results+=@{id=$case.id;question=$case.question;expectedSource=$case.expectedSource;expectedText=$case.expectedText;answerCoveragePass=$answerCoverage;mode=$r.mode;retrievalPass=$retrieval;citationPass=$cited;groundingPass=$grounded;pass=$pass;milliseconds=$timer.ElapsedMilliseconds;answer=$r.answer;sources=@($r.sources.id)}
    "$($case.id) $pass $($timer.ElapsedMilliseconds)ms $($r.mode)"
}
$directory=Join-Path $project 'submission/evidence'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$results|ConvertTo-Json -Depth 10 | Set-Content (Join-Path $directory 'rag-evaluation.json') -Encoding utf8
$times=@($results.milliseconds|Sort-Object)
$summary=@{total=$results.Count;passed=@($results|Where-Object pass).Count;medianMs=($times[9]+$times[10])/2;maxMs=($times|Measure-Object -Maximum).Maximum;evaluatedAt=(Get-Date -Format o);rubric='관련 절 검색, LLM 동작, 인용 표시, 출처 원문 일치. 의미적 완전성과 현장 정확도는 별도 평가.'}
$summary|ConvertTo-Json | Set-Content (Join-Path $directory 'rag-summary.json') -Encoding utf8
$llmTimes=@($results | Where-Object mode -eq 'llm' | ForEach-Object milliseconds | Sort-Object)
$summary.llmCases=$llmTimes.Count
if($llmTimes.Count -gt 0){ $middle=[int][Math]::Floor($llmTimes.Count/2); $summary.llmMedianMs=if($llmTimes.Count%2){$llmTimes[$middle]}else{($llmTimes[$middle-1]+$llmTimes[$middle])/2} }
$summary.rubric='관련 절 검색, LLM 동작, 인용 표시, 출처 원문 일치, 필수 답변 구절 포함. 제한된 가상 문서의 고정 질문 평가.'
$summary|ConvertTo-Json | Set-Content (Join-Path $directory 'rag-summary.json') -Encoding utf8
$summary|ConvertTo-Json

