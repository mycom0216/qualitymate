using System.Net;
using System.Net.Sockets;
using System.Text.Json;

internal static class RuntimeDiagnostics
{
    internal static async Task<int> RunAsync(string directory)
    {
        var checks = new List<DiagnosticCheck>();
        void Add(string name, string status, string detail)
        {
            checks.Add(new(name, status, detail));
            Console.WriteLine($"[{status.ToUpperInvariant()}] {name}: {detail}");
        }
        foreach (var name in new[] { "QualityMate.exe", "coreclr.dll", "appsettings.json", "knowledge/standards.json", "wwwroot/index.html", "wwwroot/polish.css", "wwwroot/polish.js", "wwwroot/rose.css" })
            Add(name, File.Exists(Path.Combine(directory, name)) ? "pass" : "fail", "Required package file; extract the complete ZIP if missing.");
        Add("Windows x64", OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 ? "pass" : "fail", "The distributed runtime requires Windows x64.");
        try
        {
            using var probe = new FileStream(Path.Combine(directory, ".qualitymate-probe-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            Add("Data write permission", "pass", "Folder is writable; existing data is unchanged.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Add("Data write permission", "fail", "Extract to a writable folder such as Documents."); }
        try
        {
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "appsettings.json")));
            var root = config.RootElement;
            var local = new Uri(root.GetProperty("Urls").GetString()!);
            var ai = root.GetProperty("Ollama");
            var model = ai.GetProperty("Model").GetString();
            var aiUri = new Uri(ai.GetProperty("BaseUrl").GetString()!);
            if (!local.IsLoopback || local.Scheme != "http" || string.IsNullOrWhiteSpace(model) || aiUri.Scheme is not ("http" or "https"))
                throw new FormatException("Invalid local configuration.");
            Add("Configuration", "pass", $"{local}; model {model}");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            try
            {
                using var tags = JsonDocument.Parse(await client.GetStringAsync(new Uri(aiUri.ToString().TrimEnd('/') + "/api/tags")));
                var ready = tags.RootElement.GetProperty("models").EnumerateArray().Any(x => x.GetProperty("name").GetString() == model);
                Add("AI model", ready ? "pass" : "warn", ready ? "Model is installed. First answer can take longer while loading." : "Model missing. Prepare with AI-setup.cmd. Retrieval-only mode remains available.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            { Add("AI model", "warn", "Ollama is unavailable or returned invalid data. Start Ollama and rerun. Retrieval-only mode remains available."); }
            var listener = new TcpListener(IPAddress.Loopback, local.Port);
            var busy = false;
            try { listener.Start(); }
            catch (SocketException) { busy = true; }
            finally { listener.Stop(); }
            if (!busy) Add("Application port", "pass", $"Port {local.Port} available. Run the application launcher next.");
            else
            {
                try
                {
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { Add("Configuration", "fail", "Check appsettings.json: local HTTP Urls and valid Ollama BaseUrl/Model are required."); }
        var failures = checks.Count(x => x.Status == "fail");
        var warnings = checks.Count(x => x.Status == "warn");
        var report = new { testedAt = DateTimeOffset.Now, failures, warnings, readyForAI = failures == 0 && warnings == 0, checks, scope = "This PC only. No model installation, database changes or verification on another PC." };
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "environment-check.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            Console.WriteLine("Report: environment-check.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Console.WriteLine("Report could not be saved. Read the checks above."); }
        Console.WriteLine($"Summary: {failures} failure(s), {warnings} warning(s).");
        return failures > 0 ? 1 : warnings > 0 ? 2 : 0;
    }
    private sealed record DiagnosticCheck(string Name, string Status, string Detail);
}
