// =====================================================================
// QualityMate 서버 진입점 (Program.cs)
// =====================================================================
// 역할: ASP.NET Core minimal API 서버를 구성하고 HTTP 엔드포인트를 모두 정의한다.
//       품질관리 RAG 도우미의 "중앙 창구"에 해당한다.
// 
// 전체 데이터 흐름
//   브라우저(wwwroot/app.js)
//     -> GET  /api/inspections                       검사 이력 조회 (InspectionStore, SQLite)
//     -> GET  /api/documents                         작업표준서(SOP) 절 목록 (KnowledgeBase)
//     -> GET  /api/status                            Ollama(로컬 LLM) 연결/모델 설치 여부
//     -> POST /api/inspections/{id}/reinspections    재검사 저장 (요청 ID 멱등성 + 버전 충돌)
//     -> POST /api/import/visionqc                   VisionQC CSV 가져오기 (전부 저장 또는 전부 취소)
//     -> POST /api/chat                              질문 -> 근거 후보 검색 -> LLM이 번호만 선택
//                                                    -> 서버가 검증된 원문을 조립해 응답
// 
// 핵심 설계 원칙: LLM은 문장을 "쓰지" 않고 후보 문장의 "번호"만 고른다.
//   서버가 번호를 검증한 뒤 표준서 원문을 그대로 돌려주므로, 모델이 없는 내용을
//   지어내는 환각(hallucination)이 답변에 섞일 수 없다.
// 
// 종료 코드(--diagnose 모드): 0 준비 완료, 1 앱 환경 오류, 2 AI 준비 필요
// (자세한 내용은 RuntimeDiagnostics.cs 참고)
// =====================================================================
using System.Text.Json;
using System.Text.RegularExpressions;

// 진단 모드: 'QualityMate.exe --diagnose'로 실행하면 웹 서버를 띄우지 않고
// 오프라인 환경 점검만 수행한 뒤 종료한다. 결과 코드는 프로세스 종료 코드로 전달된다
// (0 준비 완료 / 1 앱 환경 오류 / 2 AI 준비 필요).
if (args.Contains("--diagnose", StringComparer.Ordinal))
{
    Environment.ExitCode = await RuntimeDiagnostics.RunAsync(AppContext.BaseDirectory);
    return;
}

// 웹 서버 빌더 생성. 명령줄 인자(--urls, --DataDirectory 등)가 설정 값으로 합쳐진다.
var builder = WebApplication.CreateBuilder(args);
// DI 컨테이너 등록. 두 서비스 모두 싱글톤(앱 전체에서 1개)이다.
// InspectionStore: SQLite 검사 이력 저장소 / KnowledgeBase: 표준서 로딩과 질문 라우팅.
builder.Services.AddSingleton<InspectionStore>();
builder.Services.AddSingleton<KnowledgeBase>();
// Ollama(로컬 LLM 서버) 호출용 이름 있는 HttpClient.
// BaseUrl은 appsettings.json의 Ollama:BaseUrl, 타임아웃 90초(모델 최초 로딩이 느릴 수 있음).
builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"]!);
    client.Timeout = TimeSpan.FromSeconds(90);
});
// 여기서부터는 빌드된 앱에 미들웨어와 엔드포인트를 붙이는 단계.
var app = builder.Build();
// wwwroot의 정적 파일(index.html, app.js, css)을 서비스한다. '/' 요청은 index.html로 연결된다.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // index.html만 no-cache로 내려 새 버전의 스크립트 참조(?v=...)가 즉시 반영되게 한다.
        if (context.File.Name == "index.html")
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});
// POST /api/import/visionqc  -  VisionQC CSV 가져오기
// 요청: { "csv": "<CSV 전문 문자열>" }
// 응답: 200 {added, skipped} | 400 {error} (형식 오류) | 409 {error} (기존 검사와 내용 충돌)
// 파싱(VisionCsv.Parse)과 DB 반영(store.Import)을 분리했다. 파싱 단계에서 형식 오류가 나면
// DB를 건드리기 전에 400으로 끝나고, 저장 단계는 하나의 트랜잭션이라 전부 저장 또는 전부 취소된다.
app.MapPost("/api/import/visionqc", (CsvImportRequest request, InspectionStore store) =>
{
    // csv가 null이면 빈 문자열로 보내 VisionCsv가 'CSV는 1MB 이하로 입력하세요' 오류를 내게 한다.
    try { return store.Import(VisionCsv.Parse(request.Csv ?? "")); }
    // 예상 가능한 입력 오류(형식 불일치, 따옴표 깨짐)만 400으로 바꾼다. 그 외 예외는 500으로 두어 숨기지 않는다.
    catch (Exception e) when (e is FormatException or Microsoft.VisualBasic.FileIO.MalformedLineException)
    { return Results.BadRequest(new { error = e.Message }); }
});

// GET /api/inspections: 전체 검사 이력(제품별 최초 판정 + 재검사 목록)을 JSON 배열로 반환. 항상 200.
app.MapGet("/api/inspections", (InspectionStore store) => store.All());
// GET /api/documents: 작업표준서 절 목록(id, title, text, version, keywords). 화면 하단 '작업표준서' 영역에 표시된다.
app.MapGet("/api/documents", (KnowledgeBase kb) => kb.Sections);
// GET /api/status: AI(Ollama) 준비 상태 확인. 항상 200으로 응답하고 aiReady 값으로 구분한다.
// 화면 배지('AI 연결됨' / '검색 모드')와 --diagnose가 이 응답 형식(storage, demo)을 활용한다.
app.MapGet("/api/status", async (IHttpClientFactory clients, IConfiguration config) =>
{
    var model = config["Ollama:Model"]!;
    try
    {
        // 상태 확인은 빨리 끝나야 하므로 3초만 기다린다.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        // Ollama의 /api/tags는 설치된 모델 목록을 돌려준다.
        using var response = await clients.CreateClient("ollama").GetAsync("/api/tags", timeout.Token);
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var available = data.RootElement.GetProperty("models").EnumerateArray()
            // 설정된 모델 이름이 설치 목록에 정확히 있어야 aiReady = true.
            .Any(item => item.GetProperty("name").GetString() == model);
        return Results.Ok(new { aiReady = available, model, storage = "SQLite · 로컬 시연", demo = true });
    }
    // Ollama가 꺼져 있거나 응답 형식이 이상해도 500이 아니라 aiReady=false로 알려 화면이 '검색 모드'로 동작하게 한다.
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
    {
        return Results.Ok(new { aiReady = false, model, storage = "SQLite · 로컬 시연", demo = true });
    }
});
// POST /api/inspections/{id}/reinspections  -  재검사 기록 추가
// 요청: { result: PASS|FAIL, operator, note, expectedRevision, requestId(GUID) }
// 응답: 200 갱신된 검사 | 400 입력 오류 | 404 제품 없음 | 409 요청 ID 재사용 불일치 또는 버전 충돌
// 여기서는 입력 형식만 검사하고, 멱등성/충돌 판단은 트랜잭션 안에서 InspectionStore.Append가 한다.
app.MapPost("/api/inspections/{id}/reinspections", (string id, ReinspectionRequest request, InspectionStore store) =>
{
    // 입력 검증: 재검사 결과는 PASS/FAIL만(ERROR는 재검사 결과가 될 수 없음), 검사자·메모 필수,
    // 메모 1000자/검사자 50자 제한, 요청 ID는 빈 GUID 금지(클라이언트가 재시도용 ID를 반드시 만들어야 함).
    if (request.Result is not ("PASS" or "FAIL") || string.IsNullOrWhiteSpace(request.Operator)
        || string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 1000
        || request.Operator.Length > 50 || request.RequestId == Guid.Empty)
        return Results.BadRequest(new { error = "판정, 검사자, 메모, 요청 ID를 확인하세요." });
    return store.Append(id, request);
});
// POST /api/chat  -  근거 선택형 질의응답 (이 시스템의 핵심)
// 요청: { productId, question }
// 응답 mode 값(화면이 구분해 표시한다):
//   record         제품 판정·재검사 횟수·LOT 같은 질문. DB 사실만으로 답하며 모델을 호출하지 않는다.
//   no-evidence    표준서에 없는 주제이거나 후보 문장이 없음. 모델을 호출하지 않고 거절한다.
//   llm            모델이 고른 번호를 검증해 표준서 원문을 조립한 정상 AI 답변.
//   source-only    모델이 번호를 하나도 고르지 않음. 검색된 원문을 그대로 보여 주되 AI 답변이 아님을 표시.
//   retrieval-only 모델 연결/응답 오류. 검색된 원문만 보여 주는 대체 동작.
// 오류 코드: 400 질문이 비었거나 2000자 초과 / 404 제품 없음
app.MapPost("/api/chat", async (ChatRequest request, InspectionStore store, KnowledgeBase kb,
    IHttpClientFactory clients, IConfiguration config, CancellationToken cancellation) =>
{
    // 입력 검증: 빈 질문과 과도하게 긴 질문(2000자 초과)은 모델 호출 전에 차단한다.
    if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2000)
        return Results.BadRequest(new { error = "질문은 1~2,000자로 입력하세요." });
    // 질문은 항상 특정 제품 맥락에서 이뤄진다. 제품이 없으면 404.
    var product = store.All().FirstOrDefault(x => x.Id == request.ProductId);
    if (product is null)
        return Results.NotFound(new { error = "해당 제품의 검사 기록이 없습니다." });
    // 질문 라우팅: KnowledgeBase가 키워드 규칙으로 (a) 거절 (b) DB 기록 답변 (c) 후보 근거 문장 선택 중 하나를 정한다.
    var plan = kb.Plan(request.Question, product);
    var sources = plan.Sources;
    // DB에서 읽은 검사 사실. 모든 응답에 함께 실려 사용자가 근거를 대조할 수 있고, 모델에게도 맥락으로 전달된다. 원인은 기록되지 않으므로 '원인 확정 기록 없음'을 명시한다.
    var facts = $"제품 {product.Id}, 최초 판정 {product.InitialResult}, 검사 관찰 {product.Defect}, " +
        $"최신 판정 {product.CurrentResult}, 재검사 {product.Reinspections.Count}회. 원인 확정 기록 없음.";
    // [record 모드] DB만으로 답할 수 있는 질문은 모델 없이 즉시 응답한다(빠르고 틀릴 수 없음).
    if (plan.RecordAnswer is not null)
        return Results.Ok(new { mode = "record", answer = plan.RecordAnswer, facts, sources, model = (string?)null });
    // [no-evidence 모드] 근거 후보가 없으면 모델을 호출하지 않는다. 모델에게 억지로 답을 만들 기회를 주지 않기 위함이다.
    if (sources.Count == 0)
        return Results.Ok(new { mode = "no-evidence", answer = "관련 표준서 근거를 찾지 못했습니다. 질문을 바꾸거나 담당자에게 확인하세요.",
            facts, sources, model = (string?)null });
    // 후보 문장에 0부터 번호를 붙인다. 모델은 이 번호만 선택할 수 있다.
    var evidence = plan.Evidence.Select((item, index) => new { Index = index, item.Id, item.Text }).ToList();
    // 모델에게 보여 줄 후보 목록 형식: '번호: [절ID] 문장'.
    var context = string.Join("\n", evidence.Select(x => $"{x.Index}: [{x.Id}] {x.Text}"));
    // 시스템 프롬프트: 문장을 생성하지 말고 번호만 고르도록 지시한다. 원인을 묻는 질문에는 '원인 미확정' 문장도 유효한 답이라고 알려 준다.
    var system = "질문에 답하는 데 필요한 근거 문장의 번호를 선택하라. 답할 근거가 없으면 evidenceIds를 빈 배열로 반환하라. 복합 질문은 각 항목의 근거를 함께 선택하라. 주어진 근거만 사용하고 최대 4개를 선택하라. 출력은 JSON 객체 {\"evidenceIds\":[0,1]} 형식만 허용된다. 원인을 묻는 경우 원인 미확정 문장을 포함하라. 원인을 확정할 수 없다는 문장도 질문에 대한 유효한 답이다.";
    try
    {
        // LLM 호출 구간. 연결 실패, 타임아웃, 잘못된 JSON, 범위를 벗어난 번호는 모두 아래 catch에서 retrieval-only로 처리한다.
        var client = clients.CreateClient("ollama");
        using var response = await client.PostAsJsonAsync("/api/chat", new
        {
            model = config["Ollama:Model"], stream = false,
            // format: Ollama의 구조화 출력(JSON Schema). 출력이 {"evidenceIds":[정수,...]} 형태이고,
            // 정수는 제시된 후보 번호(enum) 중에서만, 최대 4개로 제한된다. 자유 문장 생성을 스키마 수준에서 막는다.
            format = new
            {
                type = "object", additionalProperties = false,
                properties = new { evidenceIds = new
                {
                    type = "array", minItems = 0, maxItems = 4,
                    items = new { type = "integer", @enum = evidence.Select(x => x.Index).ToArray() }
                } },
                required = new[] { "evidenceIds" }
            },
            // temperature 0으로 결정적 선택을 유도하고, 번호 목록만 필요하므로 생성 토큰을 120개로 제한한다.
            options = new { temperature = 0, num_predict = 120 },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = $"DB 사실:\n{facts}\n\n표준서 발췌:\n{context}\n\n질문:\n{request.Question}" }
            }
        }, cancellation);
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        // Ollama 응답에서 message.content(JSON 문자열)를 꺼내 다시 파싱한다.
        var content = payload.RootElement.GetProperty("message").GetProperty("content").GetString();
        using var selection = JsonDocument.Parse(content ?? "{}");
        var rawIds = selection.RootElement.GetProperty("evidenceIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        // 중복 번호는 제거한다.
        var ids = rawIds.Distinct().ToArray();
        // 서버 쪽 2차 검증: 스키마를 지켰다고 믿지 않는다. 4개 초과이거나 후보 범위 밖 번호면
        // 예외를 던져 모델 결과 전체를 버린다(검증되지 않은 선택은 사용하지 않음 -> 환각 방지).
        if (rawIds.Length > 4 || ids.Any(id => id < 0 || id >= evidence.Count))
            throw new JsonException("Invalid evidence selection");
        // [source-only 모드] 모델이 '근거 없음'(빈 배열)을 선택. 검색된 후보 원문을 직접 보여 주되 model=null, 안내문으로 AI 답변이 아님을 알린다.
        if (ids.Length == 0)
            return Results.Ok(new { mode = "source-only",
                answer = string.Join("\n", evidence.Select(e => $"{e.Text} [{e.Id}]")),
                facts, sources, notice = "AI가 근거를 선택하지 않아 검색된 표준서 원문을 직접 표시합니다.", model = (string?)null });
        // [llm 모드] 선택된 번호를 서버가 가진 원문 문장으로 치환하고 '[절ID]' 인용 표기를 붙인다. 답변 텍스트는 모델이 쓴 것이 아니라 표준서 원문 그대로다.
        var answer = string.Join("\n", ids.Select(id => $"{evidence[id].Text} [{evidence[id].Id}]"));
        return Results.Ok(new { mode = "llm", answer, facts, sources, notice = plan.Notice, model = config["Ollama:Model"] });
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
        or KeyNotFoundException or InvalidOperationException or FormatException)
    {
        // 모델 연결/응답 형식 오류(catch). 실패 원인 종류(예외 형식 이름)만 기록한다. 질문 내용이나 응답 본문은 로그에 남기지 않는다.
        app.Logger.LogWarning("AI evidence selection failed: {FailureType}", e.GetType().Name);
        // [retrieval-only 모드] AI 없이도 쓸 수 있는 대체 경로: 오류여도 200으로 검색 원문과 DB 사실을 돌려준다.
        return Results.Ok(new { mode = "retrieval-only",
            answer = "AI 연결을 사용할 수 없어 검색된 표준서 원문을 표시합니다. 아래 근거와 검사 사실을 확인하세요.",
            facts, sources, model = (string?)null });
    }
});
// 서버 시작(종료 시까지 블록). 아래는 요청/응답 및 도메인 데이터 형식(record) 정의.
app.Run();

/// <summary>POST /api/chat 요청 본문. 값이 없을 수 있어 string? 로 받고 엔드포인트에서 검증한다.</summary>
record ChatRequest(string? ProductId, string? Question);
/// <summary>재검사 저장 요청. ExpectedRevision은 사용자가 화면에서 본 재검사 횟수(낙관적 동시성 제어), RequestId는 재시도 시 중복 저장을 막는 멱등성 키다.</summary>
record ReinspectionRequest(string Result, string Operator, string Note, int ExpectedRevision, Guid RequestId);
/// <summary>저장된 재검사 1건. 최초 판정은 바꾸지 않고 이 기록을 뒤에 추가만 한다(이력 보존).</summary>
record Reinspection(Guid RequestId, string Result, string Operator, string Note, DateTimeOffset CreatedAt);
/// <summary>제품 1개의 검사 이력: 최초 판정(불변) + 재검사 목록(추가만 가능). JSON으로 그대로 화면에 전달된다.</summary>
record Inspection(string Id, string Lot, string InitialResult, string Defect, DateTimeOffset InspectedAt,
    List<Reinspection> Reinspections)
{
    // 이력 버전 = 재검사 횟수. 새 재검사가 저장될 때마다 1 증가하며 충돌 감지에 쓴다.
    public int Revision => Reinspections.Count;
    // 현재 판정 = 마지막 재검사 결과. 재검사가 없으면 최초 판정.
    public string CurrentResult => Reinspections.LastOrDefault()?.Result ?? InitialResult;
}
