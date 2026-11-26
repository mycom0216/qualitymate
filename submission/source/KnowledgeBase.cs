using System.Text.Json;
using System.Text.RegularExpressions;

record Section(string Id, string Title, string Text, string Version, string[] Keywords);
record Evidence(string Id, string Text);
record QueryPlan(List<Section> Sources, List<Evidence> Evidence, string? RecordAnswer, string? Notice);

// Rules deliberately target the four demonstration SOPs. A word shared with a SOP
// is not sufficient evidence that the SOP answers the requested operation.
sealed class KnowledgeBase
{
    public List<Section> Sections { get; }
    public KnowledgeBase(IWebHostEnvironment environment)
    {
        Sections = JsonSerializer.Deserialize<List<Section>>(
            File.ReadAllText(Path.Combine(environment.ContentRootPath, "knowledge", "standards.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("표준서가 비어 있습니다.");
    }

    public QueryPlan Plan(string question, Inspection product)
    {
        var q = Regex.Replace(question.ToLowerInvariant(), @"\s+", " ").Trim();
        bool Has(params string[] terms) => terms.Any(q.Contains);
        QueryPlan Empty(string? record = null) => new([], [], record, null);
        // These requested subjects/actions are absent from the supplied SOPs.
        if (Has("가격", "비용", "예산", "금액", "구입", "구매", "얼마인가", "얼마예", "반품", "폐기", "환불",
                "엑셀", "삭제", "날씨", "맛집", "주식", "여행", "음식", "통화", "분리수거")) return Empty();

        string Verdict(string result) => result switch { "PASS" => "합격", "FAIL" => "불합격", _ => "검사 오류" };
        if (Has("현재 판정", "최초 판정", "지금 판정", "지금 합격", "현재 합격"))
            return Empty($"{product.Id}의 최초 판정은 {Verdict(product.InitialResult)}, 현재 판정은 {Verdict(product.CurrentResult)}입니다.");
        if (Has("몇 번", "몇번", "횟수"))
            return Has("재검", "다시 검사") ? Empty($"{product.Id}의 재검사 횟수는 {product.Revision}회입니다.") : Empty();
        if (Has("로트", "lot")) return Empty($"{product.Id}의 LOT는 {product.Lot}입니다.");

        var retest = Has("재검", "다시 검사", "다시검사", "한 번 더 검사");
        var cause = Has("원인", "왜", "이유", "생긴", "생겼");
        var generalDefect = Has("불합격", "불량", "불량품");
        var explicitScratch = Has("흠집", "스크래치", "조명", "반사", "표면 손상");
        var explicitForeign = Has("이물", "렌즈", "오염");
        var scratch = explicitScratch || (!explicitForeign && generalDefect && product.Defect.Contains("흠집"));
        var foreign = explicitForeign || (!explicitScratch && generalDefect && product.Defect.Contains("이물"));
        var normal = Has("정상", "결함", "보증");
        var shipping = Has("출하", "출고", "출하 승인");
        var record = Has("기록", "적어", "적나", "작성", "남겨", "메모", "필수");
        var history = Has("이력", "보존", "최초 이력", "이전 기록");
        var storage = Has("보관", "분리");
        var before = Has("식별", "전 제품", "하기 전", "사 전");
        var check = Has("확인", "어떻게", "절차", "처리", "조치", "점검", "하나요", "해야", "알려");
        var selected = new List<Evidence>();
        void Pick(string id, string phrase)
        {
            var section = Sections.Single(x => x.Id == id);
            var text = Regex.Split(section.Text, @"(?<=다\.)\s+").Single(x => x.Contains(phrase));
            if (!selected.Any(x => x.Id == id && x.Text == text)) selected.Add(new(id, text));
        }

        string? notice = null;
        if (generalDefect && cause && !explicitScratch && !explicitForeign)
        {
            if (product.InitialResult == "ERROR") notice = "이 제품은 불합격 판정이 아니라 검사 오류입니다. 검사 오류만으로 불량 원인을 정할 수 없습니다.";
            else if (product.InitialResult == "PASS" && !product.Reinspections.Any(x => x.Result == "FAIL")) notice = "이 제품에는 불합격 기록이 없습니다. 현재 검사 기록을 확인하세요.";
            else if (!scratch && !foreign) notice = "구체적인 불량 유형이 기록되지 않아 흠집이나 이물로 판단할 수 없습니다. 담당자가 유형을 먼저 확인해야 합니다.";
            if (notice is not null && !retest) return Empty(notice);
            if (notice is not null) { scratch = false; foreign = false; }
        }
        if (retest)
        {
            if (history) Pick("SOP-03", "이력을 보존");
            if (record) Pick("SOP-03", "메모를 필수");
            if (before) Pick("SOP-03", "식별 번호");
            if (Has("조건", "동일", "유지") && product.Defect.Contains("이물")) Pick("SOP-02", "동일한 검사 조건");
            if (!record && !history && !before && !shipping && !Has("조건", "동일", "유지"))
            { Pick("SOP-03", "식별 번호"); Pick("SOP-03", "메모를 필수"); }
        }
        if (shipping)
        {
            Pick("SOP-03", "출하가 자동 승인");
            if (explicitScratch || (generalDefect && scratch)) Pick("SOP-01", "출하를 보류");
        }
        if (scratch)
        {
            if (cause) Pick("SOP-01", "발생 원인을 확정하지");
            if (Has("조명", "반사") || (!cause && !shipping && !retest && check)) Pick("SOP-01", "조명 반사");
        }
        if (foreign)
        {
            if (cause) Pick("SOP-02", "발생 원인을 확정하지");
            if (storage) Pick("SOP-02", "분리 보관");
            else if (!cause && !shipping && !retest && check) Pick("SOP-02", "오염 여부");
        }
        if (normal)
        {
            if (Has("결함", "보증", "모든", "안심")) Pick("SOP-04", "부재를 보증하지");
            else if (storage) Pick("SOP-04", "추적할 수 있도록 보관");
        }
        return new(Sections.Where(s => selected.Any(e => e.Id == s.Id)).ToList(), selected, null, notice);
    }
}
