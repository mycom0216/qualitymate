// =====================================================================
// RuntimeDiagnostics.cs  -  오프라인 환경 진단 (QualityMate.exe --diagnose)
// =====================================================================
// 역할: 발표/시연 전에 "이 PC에서 앱이 실행 가능한가"를 서버를 띄우지 않고 점검한다.
//       Program.cs가 --diagnose 인자를 보면 이 클래스의 RunAsync만 실행하고 종료한다.
// 
// 점검 항목(출력 순서)
//   1) 필수 파일 존재(실행 파일, 설정, 표준서, 화면 파일)  2) Windows x64 여부
//   3) 폴더 쓰기 권한(임시 파일을 만들고 닫으면서 자동 삭제)  4) appsettings.json 설정 값
//   5) Ollama 모델 설치 여부(경고 수준)  6) 앱 포트 사용 가능 여부
// 
// 결과
//   - 콘솔에 [PASS]/[WARN]/[FAIL] 줄을 출력하고, 같은 폴더에 environment-check.json 보고서를 저장한다.
//   - 종료 코드: 0 준비 완료 / 1 앱 환경 오류(fail 있음) / 2 AI 준비 필요(fail 없이 warn만 있음)
//   - 모델을 설치하거나 DB를 바꾸지 않는다. 읽기와 임시 쓰기 시험만 한다.
// =====================================================================
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

/// <summary>--diagnose 모드의 환경 점검 로직을 담은 정적 클래스.</summary>
internal static class RuntimeDiagnostics
{
    /// <summary>모든 점검을 순서대로 실행하고 보고서를 저장한 뒤 종료 코드를 돌려준다.</summary>
    /// <param name="directory">점검할 앱 폴더(보통 실행 파일이 있는 폴더, AppContext.BaseDirectory)</param>
    /// <returns>0 = 준비 완료, 1 = 앱 환경 오류, 2 = AI 준비 필요</returns>
    internal static async Task<int> RunAsync(string directory)
    {
        // 점검 결과 누적 목록. 마지막에 개수를 세어 종료 코드와 보고서를 만든다.
        var checks = new List<DiagnosticCheck>();
        // 지역 함수 Add: 결과를 목록에 저장하고 화면에도 바로 출력한다. status는 pass / warn / fail 중 하나.
        void Add(string name, string status, string detail)
        {
            checks.Add(new(name, status, detail));
            Console.WriteLine($"[{status.ToUpperInvariant()}] {name}: {detail}");
        }
        // 1) 필수 파일 점검: ZIP을 일부만 풀었을 때 빠지는 파일을 찾는다. 없으면 fail.
        foreach (var name in new[] { "QualityMate.exe", "coreclr.dll", "appsettings.json", "knowledge/standards.json", "wwwroot/index.html", "wwwroot/polish.css", "wwwroot/polish.js", "wwwroot/rose.css" })
            Add(name, File.Exists(Path.Combine(directory, name)) ? "pass" : "fail", "Required package file; extract the complete ZIP if missing.");
        // 2) 배포된 자체 포함 런타임은 Windows x64 전용이므로 다른 OS/아키텍처면 fail.
        Add("Windows x64", OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 ? "pass" : "fail", "The distributed runtime requires Windows x64.");
        try
        {
            // 3) 쓰기 권한 시험: 임시 파일을 만들고 DeleteOnClose로 즉시 지운다. 기존 데이터는 건드리지 않으며, 읽기 전용 위치에 풀렸다면 fail.
            using var probe = new FileStream(Path.Combine(directory, ".qualitymate-probe-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            Add("Data write permission", "pass", "Folder is writable; existing data is unchanged.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Add("Data write permission", "fail", "Extract to a writable folder such as Documents."); }
        try
        {
            // 4) 설정 점검: appsettings.json의 Urls(로컬 http), Ollama:Model, Ollama:BaseUrl을 읽는다. 외부 주소 노출이나 빈 모델 이름이면 FormatException -> 아래 catch에서 fail.
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "appsettings.json")));
            var root = config.RootElement;
            var local = new Uri(root.GetProperty("Urls").GetString()!);
            var ai = root.GetProperty("Ollama");
            var model = ai.GetProperty("Model").GetString();
            var aiUri = new Uri(ai.GetProperty("BaseUrl").GetString()!);
            // 앱 주소는 이 PC 안(loopback)의 http만 허용한다. Ollama 주소는 http/https면 허용.
            if (!local.IsLoopback || local.Scheme != "http" || string.IsNullOrWhiteSpace(model) || aiUri.Scheme is not ("http" or "https"))
                throw new FormatException("Invalid local configuration.");
            Add("Configuration", "pass", $"{local}; model {model}");
            // Ollama와 앱 상태 확인에 쓰는 클라이언트. 5초 안에 응답이 없으면 포기한다.
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            try
            {
                // 5) AI 모델 점검: Ollama /api/tags의 설치 모델 목록에 설정 모델이 있는지 본다. 없거나 Ollama가 꺼져 있으면 fail이 아니라 warn(검색 원문 모드로는 계속 사용 가능) -> 종료 코드 2.
                using var tags = JsonDocument.Parse(await client.GetStringAsync(new Uri(aiUri.ToString().TrimEnd('/') + "/api/tags")));
                var ready = tags.RootElement.GetProperty("models").EnumerateArray().Any(x => x.GetProperty("name").GetString() == model);
                Add("AI model", ready ? "pass" : "warn", ready ? "Model is installed. First answer can take longer while loading." : "Model missing. Prepare with AI-setup.cmd. Retrieval-only mode remains available.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            { Add("AI model", "warn", "Ollama is unavailable or returned invalid data. Start Ollama and rerun. Retrieval-only mode remains available."); }
            // 6) 포트 점검: 실제로 잠깐 열어 보아 사용 가능 여부를 확인한다(열리면 바로 닫음).
            var listener = new TcpListener(IPAddress.Loopback, local.Port);
            var busy = false;
            try { listener.Start(); }
            catch (SocketException) { busy = true; }
            finally { listener.Stop(); }
            // 포트가 비어 있으면 pass. 사용 중이면 아래에서 그것이 이미 실행 중인 QualityMate인지 /api/status로 확인한다.
            if (!busy) Add("Application port", "pass", $"Port {local.Port} available. Run the application launcher next.");
            else
            {
                try
                {
                    // 점유 프로세스 식별: /api/status 응답의 storage가 'SQLite'로 시작하고 demo가 true이면 QualityMate로 본다(두 번째 서버를 띄우지 말라고 안내). 아니면 모르는 서비스가 점유 중이므로 fail.
                    using var state = JsonDocument.Parse(await client.GetStringAsync(new Uri(local.ToString().TrimEnd('/') + "/api/status")));
                    var status = state.RootElement;
                    if (status.GetProperty("storage").GetString()?.StartsWith("SQLite", StringComparison.Ordinal) != true || status.GetProperty("demo").ValueKind != JsonValueKind.True)
                        throw new FormatException("Unrecognized service.");
                    Add("Application port", "pass", "QualityMate already running. Open its existing page instead of starting a second server.");
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                { Add("Application port", "fail", $"Port {local.Port} occupied by an unrecognized service. Close it or choose another local URL."); }
            }
        }
        // 설정 파일 읽기/해석 실패(파일 깨짐, 항목 누락, 잘못된 URL)는 모두 Configuration fail 하나로 알린다.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { Add("Configuration", "fail", "Check appsettings.json: local HTTP Urls and valid Ollama BaseUrl/Model are required."); }
        // 집계: fail(앱을 못 쓰는 문제)과 warn(AI만 준비 필요)을 따로 센다.
        var failures = checks.Count(x => x.Status == "fail");
        var warnings = checks.Count(x => x.Status == "warn");
        // 보고서 객체. readyForAI는 fail과 warn이 모두 0일 때만 true. scope는 이 PC에서만 점검했다는 한계를 명시한다.
        var report = new { testedAt = DateTimeOffset.Now, failures, warnings, readyForAI = failures == 0 && warnings == 0, checks, scope = "This PC only. No model installation, database changes or verification on another PC." };
        try
        {
            // 보고서 저장(environment-check.json). 쓰기 불가여도 진단 자체는 계속하고 콘솔 결과를 읽으라고 안내한다.
            await File.WriteAllTextAsync(Path.Combine(directory, "environment-check.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            Console.WriteLine("Report: environment-check.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Console.WriteLine("Report could not be saved. Read the checks above."); }
        Console.WriteLine($"Summary: {failures} failure(s), {warnings} warning(s).");
        // 종료 코드 결정: fail이 하나라도 있으면 1(앱 환경 오류), fail 없이 warn만 있으면 2(AI 준비 필요), 모두 통과하면 0(준비 완료).
        return failures > 0 ? 1 : warnings > 0 ? 2 : 0;
    }
    /// <summary>점검 1건의 결과: 이름, 상태(pass/warn/fail), 사용자에게 보여 줄 설명.</summary>
    private sealed record DiagnosticCheck(string Name, string Status, string Detail);
}
