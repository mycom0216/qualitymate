using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using System.Globalization;

static class VisionCsv
{
    public static List<Inspection> Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv) || Encoding.UTF8.GetByteCount(csv) > 1_000_000)
            throw new FormatException("CSV는 1MB 이하로 입력하세요.");
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')));
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        var header = parser.ReadFields() ?? throw new FormatException("CSV 헤더가 없습니다.");
        string[] expected = ["Id","JobId","InspectedAtUtc","OriginalVerdict","DefectScore","ElapsedMs","ImagePath","ErrorMessage"];
        if (!header.SequenceEqual(expected)) throw new FormatException("VisionQC 내보내기 CSV 헤더와 일치하지 않습니다.");
        var rows = new List<Inspection>();
        var ids = new HashSet<string>();
        while (!parser.EndOfData)
        {
            var cells = parser.ReadFields()!;
            if (cells.Length != expected.Length || !long.TryParse(cells[0], out var id) || id < 1
                || !long.TryParse(cells[1], out var job) || job < 1
                || !Regex.IsMatch(cells[2], @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")
                || !DateTimeOffset.TryParse(cells[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                || !double.TryParse(cells[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var elapsed)
                || !double.IsFinite(elapsed) || elapsed < 0)
                throw new FormatException("CSV의 식별자, 시각 또는 처리 시간을 확인하세요.");
            var result = cells[3] switch { "OK" => "PASS", "NG" => "FAIL", "ERROR" => "ERROR", _ => throw new FormatException("판정은 OK, NG, ERROR만 지원합니다.") };
            if (result != "ERROR" && (!double.TryParse(cells[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var score) || !double.IsFinite(score)))
                throw new FormatException("검사 점수가 올바르지 않습니다.");
            var key = $"VQC-{job}-{id}";
            if (!ids.Add(key)) throw new FormatException("파일 안에 중복 검사 ID가 있습니다.");
            var observation = result switch
            {
                "PASS" => "VisionQC 정상 판정",
                "FAIL" => $"VisionQC 불합격 · 구체 불량 유형 미확인 · 점수 {cells[4]}",
                _ => $"VisionQC 검사 오류 · {cells[7]}"
            };
            rows.Add(new(key, $"VQC-JOB-{job}", result, observation, at, []));
            if (rows.Count > 1000) throw new FormatException("한 번에 최대 1,000개 검사만 가져올 수 있습니다.");
        }
        if (rows.Count == 0) throw new FormatException("검사 행이 없습니다.");
        return rows;
    }
}
record CsvImportRequest(string? Csv);

