// =====================================================================
// KnowledgeBase.cs  -  작업표준서(SOP) 로딩과 질문 라우팅(규칙 기반 검색)
// =====================================================================
// 역할: knowledge/standards.json의 표준서 절을 읽어 두고, 사용자의 질문을 보고
//       "어떻게 답할지"를 결정한다(QueryPlan). Program.cs의 /api/chat이 가장 먼저 호출한다.
// 
// 질문이 도착하면 다음 세 가지 중 하나로 갈린다.
//   1) 거절        표준서에 없는 주제(가격, 날씨 등) -> 근거 후보 없음 -> 모델 호출 없이 no-evidence
//   2) 기록 답변   현재/최초 판정, 재검사 횟수, LOT -> DB 사실로 즉답(RecordAnswer) -> record 모드
//   3) 근거 후보   질문 키워드와 제품의 검사 관찰(흠집/이물)로 표준서의 해당 "문장"을 골라 후보로 제공
//                  -> Program.cs가 후보 번호를 LLM에게 주고 모델이 고른 번호만 원문으로 표시
// 
// 왜 임베딩/BM25가 아니라 키워드 규칙인가
//   이 버전은 시연용 가상 표준서 4개 절(SOP-01~04)에 맞춘 규칙 기반이다.
//   "표준서와 단어가 겹친다"는 것만으로는 그 절이 질문의 작업에 답한다고 볼 수 없어
//   (예: 재검사 질문에 흠집 절을 붙이면 오답 인용), 질문이 요구하는 주제와 행동을 직접 판별한다.
//   표준서를 늘리거나 바꾸면 규칙도 함께 점검해야 하며, 일반 검색 엔진처럼 설명하지 않는다.
// =====================================================================
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>표준서의 한 절(knowledge/standards.json의 항목). Keywords는 JSON에 있는 값이며 이 파일의 라우팅 규칙은 코드의 키워드 목록을 따로 사용한다.</summary>
record Section(string Id, string Title, string Text, string Version, string[] Keywords);
/// <summary>근거 후보 문장 1개: 어느 절(Id)의 어떤 원문 문장(Text)인지. LLM에게는 번호로 제시되고 화면에는 이 원문 그대로 표시된다.</summary>
record Evidence(string Id, string Text);
/// <summary>
/// 질문 처리 계획. Sources: 화면에 보여 줄 관련 절 / Evidence: LLM이 고를 후보 문장들 /
/// RecordAnswer: DB 사실로 바로 답한 문장(있으면 모델 호출 안 함) / Notice: 답변 위에 붙일 안내문.
/// </summary>
record QueryPlan(List<Section> Sources, List<Evidence> Evidence, string? RecordAnswer, string? Notice);

// (위 파일 머리말 참고) 아래 규칙은 의도적으로 시연용 SOP 4개에 맞춘 것이다.
// Rules deliberately target the four demonstration SOPs. A word shared with a SOP
// is not sufficient evidence that the SOP answers the requested operation.
/// <summary>표준서를 메모리에 보관하고 질문마다 QueryPlan을 만드는 서비스(싱글톤).</summary>
sealed class KnowledgeBase
{
    // 읽어 둔 표준서 절 전체. /api/documents가 그대로 반환한다.
    public List<Section> Sections { get; }
    /// <summary>앱 시작 시 knowledge/standards.json을 읽는다. 파일이 없거나 비어 있으면 예외로 시작 자체를 막아 표준서 없는 상태로 답하는 일을 방지한다.</summary>
    /// <param name="environment">앱 실행 폴더 위치(ContentRootPath)</param>
    public KnowledgeBase(IWebHostEnvironment environment)
    {
        Sections = JsonSerializer.Deserialize<List<Section>>(
            File.ReadAllText(Path.Combine(environment.ContentRootPath, "knowledge", "standards.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("표준서가 비어 있습니다.");
    }

    /// <summary>
    /// 질문을 분석해 처리 계획을 만든다. 모델을 호출하지 않는 순수한 규칙 함수라 같은 입력에는 항상 같은 결과가 나온다.
    /// </summary>
    /// <param name="question">사용자 질문(이미 길이 검증됨)</param>
    /// <param name="product">질문 대상 제품. 판정/횟수/LOT 답변과 흠집·이물 구분에 사용한다.</param>
    /// <returns>거절(빈 후보), 기록 답변, 또는 근거 후보를 담은 QueryPlan</returns>
    public QueryPlan Plan(string question, Inspection product)
    {
        // 질문 정규화: 소문자로 바꾸고 연속 공백을 하나로 줄여 '재검 사' 같은 띄어쓰기 차이에 덜 민감하게 만든다.
        var q = Regex.Replace(question.ToLowerInvariant(), @"\s+", " ").Trim();
        // 지역 함수 Has: 정규화된 질문에 주어진 단어(부분 문자열) 중 하나라도 들어 있는지 검사한다. 형태소 분석 없이 단순 포함 검사만 한다.
        bool Has(params string[] terms) => terms.Any(q.Contains);
        // 지역 함수 Empty: 근거 후보가 없는 계획을 만든다. record가 있으면 DB 기록 답변, 없으면 '거절/근거 없음'.
        QueryPlan Empty(string? record = null) => new([], [], record, null);
        // 1단계 - 범위 밖 주제 거절: 가격, 예산, 반품, 폐기 등 공급된 표준서에 없는 주제/행동은 후보 없이 즉시 반환한다.
        // 이유: (1) 표준서에 답이 없는데 모델에게 물으면 비슷한 문장을 억지로 골라 오답 인용을 만들 수 있고,
        //       (2) 호출을 아예 하지 않으면 빠르고 비용이 없으며 결과가 항상 같다.
        // 키워드 목록은 현재 시연 SOP에서 다루지 않는 주제를 사람이 열거한 것이다(목록에 없는 무관 질문은 아래 규칙에서 후보가 안 잡혀 no-evidence가 된다).
        // These requested subjects/actions are absent from the supplied SOPs.
        if (Has("가격", "비용", "예산", "금액", "구입", "구매", "얼마인가", "얼마예", "반품", "폐기", "환불",
                "엑셀", "삭제", "날씨", "맛집", "주식", "여행", "음식", "통화", "분리수거")) return Empty();

        // 판정 코드를 사용자에게 보여 줄 한국어로 변환한다(PASS 합격 / FAIL 불합격 / 그 외 검사 오류).
        string Verdict(string result) => result switch { "PASS" => "합격", "FAIL" => "불합격", _ => "검사 오류" };
        // 2단계 - DB 기록 질문: 판정, 재검사 횟수, LOT는 표준서가 아니라 DB에 있는 사실이므로 모델 없이 DB 값으로 직접 답한다(record 모드).
        if (Has("현재 판정", "최초 판정", "지금 판정", "지금 합격", "현재 합격"))
            return Empty($"{product.Id}의 최초 판정은 {Verdict(product.InitialResult)}, 현재 판정은 {Verdict(product.CurrentResult)}입니다.");
        // '몇 번/횟수'는 재검사 맥락일 때만 횟수를 답하고, 그 외에는 거절한다(다른 횟수 질문에 잘못 답하지 않도록).
        if (Has("몇 번", "몇번", "횟수"))
            return Has("재검", "다시 검사") ? Empty($"{product.Id}의 재검사 횟수는 {product.Revision}회입니다.") : Empty();
        if (Has("로트", "lot")) return Empty($"{product.Id}의 LOT는 {product.Lot}입니다.");

        // 3단계 - 질문 의도 플래그: 질문에 어떤 주제(재검사, 원인, 흠집, 이물, 출하 등)와 행동이 있는지 키워드로 표시한다. 아래 Pick 규칙이 이 조합으로 문장을 고른다.
        var retest = Has("재검", "다시 검사", "다시검사", "한 번 더 검사");
        var cause = Has("원인", "왜", "이유", "생긴", "생겼");
        // generalDefect는 '불합격/불량' 같은 일반 표현. 구체 유형(흠집/이물)이 질문에 없으면 제품의 검사 관찰(Defect) 기록으로 유형을 추정한다.
        var generalDefect = Has("불합격", "불량", "불량품");
        var explicitScratch = Has("흠집", "스크래치", "조명", "반사", "표면 손상");
        var explicitForeign = Has("이물", "렌즈", "오염");
        // scratch/foreign: 질문에 유형이 명시되면 그것을 우선하고, 일반 표현이면 제품 기록(Defect)에 '흠집'/'이물'이 있을 때만 그 유형으로 본다.
        // 기록에 유형이 없으면(예: VisionQC 불합격은 구체 유형 미확인) 어느 쪽도 아니므로 유형을 지어내지 않는다.
        var scratch = explicitScratch || (!explicitForeign && generalDefect && product.Defect.Contains("흠집"));
        var foreign = explicitForeign || (!explicitScratch && generalDefect && product.Defect.Contains("이물"));
        var normal = Has("정상", "결함", "보증");
        var shipping = Has("출하", "출고", "출하 승인");
        var record = Has("기록", "적어", "적나", "작성", "남겨", "메모", "필수");
        var history = Has("이력", "보존", "최초 이력", "이전 기록");
        var storage = Has("보관", "분리");
        var before = Has("식별", "전 제품", "하기 전", "사 전");
        // check: '어떻게/절차/조치/확인' 같은 절차 문의 표현. 다른 의도(원인, 출하, 재검사)가 없을 때 기본 확인 절차 문장을 고르는 조건에 쓴다.
        var check = Has("확인", "어떻게", "절차", "처리", "조치", "점검", "하나요", "해야", "알려");
        // 선택된 후보 문장 목록(중복 없이 쌓인다).
        var selected = new List<Evidence>();
        // 지역 함수 Pick: 절 id의 본문을 문장('...다.' 뒤 공백)으로 나눠 phrase를 포함하는 문장 "하나"를 후보에 추가한다.
        // Single()이므로 구절이 문장 하나에만 있어야 하며, 표준서 문구가 바뀌어 0개나 2개 이상이 되면 예외가 나 규칙 점검이 필요함을 바로 드러낸다.
        // 절 전체가 아니라 필요한 문장만 고르는 이유: 질문과 무관한 문장이 답변에 섞이지 않게 하기 위함이다.
        void Pick(string id, string phrase)
        {
            var section = Sections.Single(x => x.Id == id);
            var text = Regex.Split(section.Text, @"(?<=다\.)\s+").Single(x => x.Contains(phrase));
            if (!selected.Any(x => x.Id == id && x.Text == text)) selected.Add(new(id, text));
        }

        // notice: 후보와 함께(또는 후보 없이) 화면에 보여 줄 주의 문구. 불량 원인 질문에 제품 상태가 맞지 않을 때 설정한다.
        string? notice = null;
        // 4단계 - 원인 질문 사전 점검: 유형 없이 '불량 원인'을 묻는 경우 제품 상태에 따라 안내한다.
        //  - 검사 오류(ERROR): 불합격이 아니므로 원인을 말할 수 없음.
        //  - 최초 PASS이고 FAIL 재검사도 없음: 불합격 기록 자체가 없음.
        //  - 불량 유형이 기록되지 않음: 흠집/이물로 단정하지 않고 담당자 확인을 안내.
        // 재검사 질문이 함께 있지 않으면 안내문만 반환(후보 없음)하고, 함께 있으면 유형 추정만 끄고 재검사 문장은 계속 고른다.
        if (generalDefect && cause && !explicitScratch && !explicitForeign)
        {
            if (product.InitialResult == "ERROR") notice = "이 제품은 불합격 판정이 아니라 검사 오류입니다. 검사 오류만으로 불량 원인을 정할 수 없습니다.";
            else if (product.InitialResult == "PASS" && !product.Reinspections.Any(x => x.Result == "FAIL")) notice = "이 제품에는 불합격 기록이 없습니다. 현재 검사 기록을 확인하세요.";
            else if (!scratch && !foreign) notice = "구체적인 불량 유형이 기록되지 않아 흠집이나 이물로 판단할 수 없습니다. 담당자가 유형을 먼저 확인해야 합니다.";
            if (notice is not null && !retest) return Empty(notice);
            if (notice is not null) { scratch = false; foreign = false; }
        }
        // 5단계 - 재검사 질문: 이력 보존/메모 필수/식별 번호 문장은 SOP-03에서, 이물 제품의 동일 검사 조건 문장은 SOP-02에서 고른다. 세부 키워드가 없으면 기본 두 문장(식별 번호, 메모 필수)을 제공한다.
        if (retest)
        {
            if (history) Pick("SOP-03", "이력을 보존");
            if (record) Pick("SOP-03", "메모를 필수");
            if (before) Pick("SOP-03", "식별 번호");
            if (Has("조건", "동일", "유지") && product.Defect.Contains("이물")) Pick("SOP-02", "동일한 검사 조건");
            if (!record && !history && !before && !shipping && !Has("조건", "동일", "유지"))
            { Pick("SOP-03", "식별 번호"); Pick("SOP-03", "메모를 필수"); }
        }
        // 6단계 - 출하 질문: 재검사 후 출하가 자동 승인되지 않는다는 SOP-03 문장과, 흠집이 관련되면 출하 보류(SOP-01) 문장을 고른다.
        if (shipping)
        {
            Pick("SOP-03", "출하가 자동 승인");
            if (explicitScratch || (generalDefect && scratch)) Pick("SOP-01", "출하를 보류");
        }
        // 7단계 - 흠집 제품: 원인 질문이면 '원인 미확정' 문장, 조명/반사 질문이거나 일반 확인 절차 질문이면 조명 반사 확인 문장(SOP-01).
        if (scratch)
        {
            if (cause) Pick("SOP-01", "발생 원인을 확정하지");
            if (Has("조명", "반사") || (!cause && !shipping && !retest && check)) Pick("SOP-01", "조명 반사");
        }
        // 8단계 - 이물 제품: 원인 질문이면 '원인 미확정' 문장, 보관 질문이면 분리 보관, 일반 확인 절차 질문이면 오염 확인 문장(SOP-02).
        if (foreign)
        {
            if (cause) Pick("SOP-02", "발생 원인을 확정하지");
            if (storage) Pick("SOP-02", "분리 보관");
            else if (!cause && !shipping && !retest && check) Pick("SOP-02", "오염 여부");
        }
        // 9단계 - 정상 제품 문의: 결함 부재를 보증하지 않는다는 문장 또는 추적 가능한 보관 문장(SOP-04).
        if (normal)
        {
            if (Has("결함", "보증", "모든", "안심")) Pick("SOP-04", "부재를 보증하지");
            else if (storage) Pick("SOP-04", "추적할 수 있도록 보관");
        }
        // 선택된 문장이 속한 절만 Sources로 모아 반환한다. 선택된 문장이 하나도 없으면 후보가 비어 Program.cs가 no-evidence로 처리한다.
        return new(Sections.Where(s => selected.Any(e => e.Id == s.Id)).ToList(), selected, null, notice);
    }
}
