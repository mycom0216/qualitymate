# =====================================================================
# evaluate.ps1  -  고정 질문 20개 RAG 평가
# =====================================================================
# 목적  : tools/questions.json의 질문 20개를 실행 중인 서버(/api/chat)에 보내
#         (1) 기대한 표준서 절 검색 (2) LLM 모드 동작 (3) [SOP-n] 인용 (4) 답변 문장이 출처 원문과 일치
#         (5) 필수 구절 포함 여부를 확인하고 응답 시간을 잰다. 근거가 없어야 하는 질문은 no-evidence인지 본다.
# 입력  : -BaseUrl (기본 http://127.0.0.1:5187). 서버와 Ollama가 미리 실행 중이어야 한다.
# 출력  : submission/evidence/rag-evaluation.json(질문별 결과), rag-summary.json(통과 수, 시간 요약).
# 실행  : 서버 실행 후 ./tools/evaluate.ps1
# 한계  : 정해진 가상 문서와 고정 질문의 검사이며 일반적인 현장 정확도를 뜻하지 않는다.
# =====================================================================
param([string]$BaseUrl='http://127.0.0.1:5187')
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
# 질문 목록 읽기. 각 항목: id, productId, question, expectedSource(없으면 거절 기대), expectedText(필수 구절).
$cases=Get-Content (Join-Path $PSScriptRoot 'questions.json') -Raw | ConvertFrom-Json
$results=@()
# 질문마다 응답 시간을 재며 호출하고 아래 기준을 판정한다.
foreach($case in $cases){
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $body=@{productId=$case.productId;question=$case.question}|ConvertTo-Json
    $r=Invoke-RestMethod "$BaseUrl/api/chat" -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
    $timer.Stop()
    # 검색 기준: 기대 절이 sources에 있거나, 기대 절이 없는 질문이면 no-evidence.
    $retrieval=if($case.expectedSource){$r.sources.id -contains $case.expectedSource}else{$r.mode -eq 'no-evidence'}
    $mode=if($case.expectedSource){$r.mode -eq 'llm'}else{$r.mode -eq 'no-evidence'}
    $cited=if($case.expectedSource){$r.answer -match '\[SOP-\d+\]'}else{$true}
    # 원문 일치 검사(llm 모드만): 답변의 각 줄이 '문장 [절ID]' 형식이고 그 문장이 해당 절 원문에 그대로 들어 있어야 한다.
    $grounded=$true
    if($r.mode -eq 'llm'){
        foreach($line in ($r.answer -split "\n" | Where-Object { $_.Trim() })){
            if($line -notmatch '^(.*) \[(SOP-\d+)\]$'){$grounded=$false;continue}
            $text=$Matches[1];$id=$Matches[2]
            $source=$r.sources | Where-Object id -eq $id
            if(-not $source -or -not $source.text.Contains($text)){$grounded=$false}
        }
    }
    # 필수 구절 포함 여부.
    $answerCoverage=if($case.expectedText){$r.answer.Contains($case.expectedText)}else{$true}
    # 다섯 기준을 모두 만족해야 통과.
    $pass=$retrieval -and $mode -and $cited -and $grounded -and $answerCoverage
    $results+=@{id=$case.id;question=$case.question;expectedSource=$case.expectedSource;expectedText=$case.expectedText;answerCoveragePass=$answerCoverage;mode=$r.mode;retrievalPass=$retrieval;citationPass=$cited;groundingPass=$grounded;pass=$pass;milliseconds=$timer.ElapsedMilliseconds;answer=$r.answer;sources=@($r.sources.id)}
    "$($case.id) $pass $($timer.ElapsedMilliseconds)ms $($r.mode)"
}
$directory=Join-Path $project 'submission/evidence'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$results|ConvertTo-Json -Depth 10 | Set-Content (Join-Path $directory 'rag-evaluation.json') -Encoding utf8
$times=@($results.milliseconds|Sort-Object)
# 요약: 통과 수, 전체/최대 시간 등. 아래에서 LLM 호출 질문만의 중앙값(llmMedianMs)을 따로 계산해 덧붙인다.
$summary=@{total=$results.Count;passed=@($results|Where-Object pass).Count;medianMs=($times[9]+$times[10])/2;maxMs=($times|Measure-Object -Maximum).Maximum;evaluatedAt=(Get-Date -Format o);rubric='관련 절 검색, LLM 동작, 인용 표시, 출처 원문 일치. 의미적 완전성과 현장 정확도는 별도 평가.'}
$summary|ConvertTo-Json | Set-Content (Join-Path $directory 'rag-summary.json') -Encoding utf8
$llmTimes=@($results | Where-Object mode -eq 'llm' | ForEach-Object milliseconds | Sort-Object)
$summary.llmCases=$llmTimes.Count
if($llmTimes.Count -gt 0){ $middle=[int][Math]::Floor($llmTimes.Count/2); $summary.llmMedianMs=if($llmTimes.Count%2){$llmTimes[$middle]}else{($llmTimes[$middle-1]+$llmTimes[$middle])/2} }
$summary.rubric='관련 절 검색, LLM 동작, 인용 표시, 출처 원문 일치, 필수 답변 구절 포함. 제한된 가상 문서의 고정 질문 평가.'
$summary|ConvertTo-Json | Set-Content (Join-Path $directory 'rag-summary.json') -Encoding utf8
$summary|ConvertTo-Json

