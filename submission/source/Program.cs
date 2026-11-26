using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Contains("--diagnose", StringComparer.Ordinal))
{
    Environment.ExitCode = await RuntimeDiagnostics.RunAsync(AppContext.BaseDirectory);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<InspectionStore>();
builder.Services.AddSingleton<KnowledgeBase>();
builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"]!);
    client.Timeout = TimeSpan.FromSeconds(90);
});
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name == "index.html")
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.MapPost("/api/import/visionqc", (CsvImportRequest request, InspectionStore store) =>
{
    try { return store.Import(VisionCsv.Parse(request.Csv ?? "")); }
    catch (Exception e) when (e is FormatException or Microsoft.VisualBasic.FileIO.MalformedLineException)
    { return Results.BadRequest(new { error = e.Message }); }
});

app.MapGet("/api/inspections", (InspectionStore store) => store.All());
app.MapGet("/api/documents", (KnowledgeBase kb) => kb.Sections);
app.MapGet("/api/status", async (IHttpClientFactory clients, IConfiguration config) =>
{
    var model = config["Ollama:Model"]!;
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var response = await clients.CreateClient("ollama").GetAsync("/api/tags", timeout.Token);
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var available = data.RootElement.GetProperty("models").EnumerateArray()
            .Any(item => item.GetProperty("name").GetString() == model);
        return Results.Ok(new { aiReady = available, model, storage = "SQLite · 로컬 시연", demo = true });
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
    {
        return Results.Ok(new { aiReady = false, model, storage = "SQLite · 로컬 시연", demo = true });
    }
});
app.MapPost("/api/inspections/{id}/reinspections", (string id, ReinspectionRequest request, InspectionStore store) =>
{
    if (request.Result is not ("PASS" or "FAIL") || string.IsNullOrWhiteSpace(request.Operator)
        || string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 1000
        || request.Operator.Length > 50 || request.RequestId == Guid.Empty)
        return Results.BadRequest(new { error = "판정, 검사자, 메모, 요청 ID를 확인하세요." });
    return store.Append(id, request);
});
app.MapPost("/api/chat", async (ChatRequest request, InspectionStore store, KnowledgeBase kb,
    IHttpClientFactory clients, IConfiguration config, CancellationToken cancellation) =>
{
    if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2000)
        return Results.BadRequest(new { error = "질문은 1~2,000자로 입력하세요." });
    var product = store.All().FirstOrDefault(x => x.Id == request.ProductId);
    if (product is null)
        return Results.NotFound(new { error = "해당 제품의 검사 기록이 없습니다." });
    var plan = kb.Plan(request.Question, product);
    var sources = plan.Sources;
    var facts = $"제품 {product.Id}, 최초 판정 {product.InitialResult}, 검사 관찰 {product.Defect}, " +
        $"최신 판정 {product.CurrentResult}, 재검사 {product.Reinspections.Count}회. 원인 확정 기록 없음.";
    if (plan.RecordAnswer is not null)
        return Results.Ok(new { mode = "record", answer = plan.RecordAnswer, facts, sources, model = (string?)null });
    if (sources.Count == 0)
        return Results.Ok(new { mode = "no-evidence", answer = "관련 표준서 근거를 찾지 못했습니다. 질문을 바꾸거나 담당자에게 확인하세요.",
            facts, sources, model = (string?)null });
    var evidence = plan.Evidence.Select((item, index) => new { Index = index, item.Id, item.Text }).ToList();
    var context = string.Join("\n", evidence.Select(x => $"{x.Index}: [{x.Id}] {x.Text}"));
    var system = "질문에 답하는 데 필요한 근거 문장의 번호를 선택하라. 답할 근거가 없으면 evidenceIds를 빈 배열로 반환하라. 복합 질문은 각 항목의 근거를 함께 선택하라. 주어진 근거만 사용하고 최대 4개를 선택하라. 출력은 JSON 객체 {\"evidenceIds\":[0,1]} 형식만 허용된다. 원인을 묻는 경우 원인 미확정 문장을 포함하라. 원인을 확정할 수 없다는 문장도 질문에 대한 유효한 답이다.";
    try
    {
        var client = clients.CreateClient("ollama");
        using var response = await client.PostAsJsonAsync("/api/chat", new
        {
            model = config["Ollama:Model"], stream = false,
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
            options = new { temperature = 0, num_predict = 120 },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = $"DB 사실:\n{facts}\n\n표준서 발췌:\n{context}\n\n질문:\n{request.Question}" }
            }
        }, cancellation);
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var content = payload.RootElement.GetProperty("message").GetProperty("content").GetString();
        using var selection = JsonDocument.Parse(content ?? "{}");
        var rawIds = selection.RootElement.GetProperty("evidenceIds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var ids = rawIds.Distinct().ToArray();
        if (rawIds.Length > 4 || ids.Any(id => id < 0 || id >= evidence.Count))
            throw new JsonException("Invalid evidence selection");
        if (ids.Length == 0)
            return Results.Ok(new { mode = "source-only",
                answer = string.Join("\n", evidence.Select(e => $"{e.Text} [{e.Id}]")),
                facts, sources, notice = "AI가 근거를 선택하지 않아 검색된 표준서 원문을 직접 표시합니다.", model = (string?)null });
        var answer = string.Join("\n", ids.Select(id => $"{evidence[id].Text} [{evidence[id].Id}]"));
        return Results.Ok(new { mode = "llm", answer, facts, sources, notice = plan.Notice, model = config["Ollama:Model"] });
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
        or KeyNotFoundException or InvalidOperationException or FormatException)
    {
        app.Logger.LogWarning("AI evidence selection failed: {FailureType}", e.GetType().Name);
        return Results.Ok(new { mode = "retrieval-only",
            answer = "AI 연결을 사용할 수 없어 검색된 표준서 원문을 표시합니다. 아래 근거와 검사 사실을 확인하세요.",
            facts, sources, model = (string?)null });
    }
});
app.Run();

record ChatRequest(string? ProductId, string? Question);
record ReinspectionRequest(string Result, string Operator, string Note, int ExpectedRevision, Guid RequestId);
record Reinspection(Guid RequestId, string Result, string Operator, string Note, DateTimeOffset CreatedAt);
record Inspection(string Id, string Lot, string InitialResult, string Defect, DateTimeOffset InspectedAt,
    List<Reinspection> Reinspections)
{
    public int Revision => Reinspections.Count;
    public string CurrentResult => Reinspections.LastOrDefault()?.Result ?? InitialResult;
}
