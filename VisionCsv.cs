// =====================================================================
// VisionCsv.cs  -  VisionQC 내보내기 CSV 파서
// =====================================================================
// 역할: VisionQC가 내보낸 검사 CSV 문자열을 읽어 QualityMate의 Inspection 목록으로 변환한다.
//       DB에는 쓰지 않는다. 형식 검증과 변환만 하고, 저장은 InspectionStore.Import가 맡는다.
// 
// 데이터 흐름
//   POST /api/import/visionqc { csv }  ->  VisionCsv.Parse(csv)  ->  List<Inspection>
//                                       ->  InspectionStore.Import(...)  (전부 저장 또는 전부 취소)
// 
// 원칙: 한 줄이라도 형식이 맞지 않으면 FormatException을 던져 파일 전체를 거부한다(일부만 가져오지 않음).
//        Program.cs가 이 예외를 400 응답으로 바꾼다.
// =====================================================================
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using System.Globalization;

/// <summary>VisionQC CSV를 검증하며 Inspection 목록으로 바꾸는 정적 도우미.</summary>
static class VisionCsv
{
    /// <summary>
    /// CSV 전문을 검사 목록으로 변환한다.
    /// 변환 규칙: 검사 ID는 VQC-{JobId}-{Id}, LOT는 VQC-JOB-{JobId},
    /// 판정은 OK -> PASS, NG -> FAIL, ERROR -> ERROR(검사 오류로 유지, 불합격으로 바꾸지 않음).
    /// </summary>
    /// <param name="csv">UTF-8 CSV 본문(맨 앞 BOM이 있어도 허용)</param>
    /// <returns>1~1,000개의 Inspection(재검사 없음)</returns>
    /// <exception cref="FormatException">크기 초과, 헤더 불일치, 값 오류, 파일 내 중복, 행 수 초과(0개/1,000개 초과) 등</exception>
    public static List<Inspection> Parse(string csv)
    {
        // 크기 제한: 비었거나 UTF-8 바이트 수가 1,000,000을 넘으면 거부(문자 수가 아니라 바이트 기준이므로 한글도 정확히 제한된다).
        if (string.IsNullOrWhiteSpace(csv) || Encoding.UTF8.GetByteCount(csv) > 1_000_000)
            throw new FormatException("CSV는 1MB 이하로 입력하세요.");
        // 따옴표로 감싼 필드(쉼표가 들어간 오류 메시지 등)를 올바르게 읽기 위해 직접 split 하지 않고 TextFieldParser를 쓴다. BOM(\uFEFF)은 제거한다.
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')));
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        // 헤더 검증: VisionQC.Core/InspectionCsv.cs가 내보내는 8개 열과 이름·순서가 정확히 같아야 한다. 다른 형식의 파일을 잘못 가져오는 것을 막는다.
        var header = parser.ReadFields() ?? throw new FormatException("CSV 헤더가 없습니다.");
        string[] expected = ["Id","JobId","InspectedAtUtc","OriginalVerdict","DefectScore","ElapsedMs","ImagePath","ErrorMessage"];
        if (!header.SequenceEqual(expected)) throw new FormatException("VisionQC 내보내기 CSV 헤더와 일치하지 않습니다.");
        // rows: 변환 결과. ids: 같은 파일 안에서 검사 키가 중복되는지 확인하는 집합.
        var rows = new List<Inspection>();
        var ids = new HashSet<string>();
        // 데이터 행 반복: 한 행씩 검증하고 변환한다.
        while (!parser.EndOfData)
        {
            var cells = parser.ReadFields()!;
            // 행 검증(하나라도 어긋나면 파일 전체 거부):
            //  - 열 개수 일치, Id와 JobId는 1 이상의 정수,
            //  - 검사 시각은 'YYYY-MM-DDThh:mm:ss' 형식이며 반드시 시간대(Z 또는 +hh:mm)를 포함(정규식) -> 시간대 없는 시각은 해석이 모호하므로 거부,
            //  - 처리 시간(ElapsedMs)은 유한한 0 이상의 숫자.
            if (cells.Length != expected.Length || !long.TryParse(cells[0], out var id) || id < 1
                || !long.TryParse(cells[1], out var job) || job < 1
                || !Regex.IsMatch(cells[2], @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")
                || !DateTimeOffset.TryParse(cells[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                || !double.TryParse(cells[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var elapsed)
                || !double.IsFinite(elapsed) || elapsed < 0)
                throw new FormatException("CSV의 식별자, 시각 또는 처리 시간을 확인하세요.");
            // 원본 판정 변환. OK/NG/ERROR 외의 값은 지원하지 않는다.
            var result = cells[3] switch { "OK" => "PASS", "NG" => "FAIL", "ERROR" => "ERROR", _ => throw new FormatException("판정은 OK, NG, ERROR만 지원합니다.") };
            // ERROR 행은 점수가 비어 있을 수 있으므로 점수 검사를 하지 않고, OK/NG 행만 유한한 숫자 점수를 요구한다.
            if (result != "ERROR" && (!double.TryParse(cells[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var score) || !double.IsFinite(score)))
                throw new FormatException("검사 점수가 올바르지 않습니다.");
            // 검사 ID: JobId와 Id를 합쳐 다른 데이터(QM-001 등)와 겹치지 않는 키를 만든다. 같은 파일 안에서 중복되면 거부한다.
            var key = $"VQC-{job}-{id}";
            if (!ids.Add(key)) throw new FormatException("파일 안에 중복 검사 ID가 있습니다.");
            // 검사 관찰 문구. NG는 구체 불량 유형을 CSV로 알 수 없으므로 '유형 미확인'으로 명시한다(흠집/이물을 추측해 지어내지 않음 -> KnowledgeBase가 유형 확정 없이 안내하게 하는 근거).
            var observation = result switch
            {
                "PASS" => "VisionQC 정상 판정",
                "FAIL" => $"VisionQC 불합격 · 구체 불량 유형 미확인 · 점수 {cells[4]}",
                _ => $"VisionQC 검사 오류 · {cells[7]}"
            };
            // 검사 1건 생성: 재검사 목록은 빈 상태로 시작한다.
            rows.Add(new(key, $"VQC-JOB-{job}", result, observation, at, []));
            // 한 번에 최대 1,000건까지만 허용(과도한 입력 방지).
            if (rows.Count > 1000) throw new FormatException("한 번에 최대 1,000개 검사만 가져올 수 있습니다.");
        }
        // 헤더만 있고 데이터가 없는 파일은 오류로 처리한다.
        if (rows.Count == 0) throw new FormatException("검사 행이 없습니다.");
        return rows;
    }
}
/// <summary>POST /api/import/visionqc 요청 본문. Csv는 CSV 파일의 전체 텍스트다(브라우저가 파일을 읽어 문자열로 보냄).</summary>
record CsvImportRequest(string? Csv);

