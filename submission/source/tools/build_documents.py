from pathlib import Path
import json, html, copy, re
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, PageBreak, Table, TableStyle, Image
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib import colors
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4

root=Path(__file__).resolve().parents[1]
out=root/"submission"/"documents"; out.mkdir(parents=True,exist_ok=True)
ev=root/"submission"/"evidence"
summary=json.loads((ev/"rag-summary.json").read_text(encoding="utf-8-sig"))
results=json.loads((ev/"rag-evaluation.json").read_text(encoding="utf-8-sig"))
pdfmetrics.registerFont(TTFont("Malgun","C:/Windows/Fonts/malgun.ttf"))
pdfmetrics.registerFont(TTFont("MalgunBold","C:/Windows/Fonts/malgunbd.ttf"))
body=ParagraphStyle("body",fontName="Malgun",fontSize=10.5,leading=17,wordWrap="CJK",spaceAfter=10)
small=ParagraphStyle("small",parent=body,fontSize=8.5,leading=13,spaceAfter=5)
h1=ParagraphStyle("h1",parent=body,fontName="MalgunBold",fontSize=22,leading=32,spaceAfter=23,textColor=colors.HexColor("#16382c"))
h2=ParagraphStyle("h2",parent=body,fontName="MalgunBold",fontSize=13,leading=20,spaceBefore=12,spaceAfter=8,textColor=colors.HexColor("#31583d"))
title=ParagraphStyle("title",parent=h1,fontSize=32,leading=45,spaceAfter=30)
def p(text,style=body): return Paragraph(html.escape(str(text)).replace("\n","<br/>"),style)
def table(rows,widths,header=True):
    t=Table([[p(v,small) for v in row] for row in rows],colWidths=widths,repeatRows=1 if header else 0,hAlign="LEFT")
    t.setStyle(TableStyle([("BACKGROUND",(0,0),(-1,0),colors.HexColor("#e5edde")),("VALIGN",(0,0),(-1,-1),"TOP"),("GRID",(0,0),(-1,-1),.4,colors.HexColor("#ccd8ca")),("LEFTPADDING",(0,0),(-1,-1),8),("RIGHTPADDING",(0,0),(-1,-1),8),("TOPPADDING",(0,0),(-1,-1),7),("BOTTOMPADDING",(0,0),(-1,-1),7)]))
    return t
def footer(c,doc):
    c.setStrokeColor(colors.HexColor("#c8d4c8"));c.line(48,42,547,42)
    c.setFont("Malgun",8);c.setFillColor(colors.HexColor("#617064"))
    c.drawString(48,28,"QualityMate · 인공지능 응용 시스템 개발 종합 프로젝트")
    c.drawRightString(547,28,str(doc.page))
def write(name,story):
    SimpleDocTemplate(str(out/name),pagesize=A4,rightMargin=48,leftMargin=48,topMargin=47,bottomMargin=57,title=name.removesuffix(".pdf"),author="").build(copy.deepcopy(story),onFirstPage=footer,onLaterPages=footer)
def page(story,heading):
    if story: story.append(PageBreak())
    story.append(p(heading,h1))
report=[]
report += [Spacer(1,54),p("QualityMate",title),p("검사 이력과 작업표준서를 연계한\nRAG 기반 품질관리 지원 시스템",h1),p("최종 프로젝트 결과보고서",h2),p("작성·검증 기준 2026년 11월 20일\n최종 발표 예정 2026년 12월 30일\n프로젝트 형태 개인 프로젝트")]
report += [Spacer(1,28),table([["검증 항목","실측 결과"],["기능 검사","17개 통과"],["RAG 고정 질문",f"{summary['passed']} / {summary['total']} 통과"],["LLM 호출 질문 중앙값",f"{summary['llmMedianMs']/1000:.2f}초 · 16개 질문"],["전체 질문 최대 응답",f"{summary['maxMs']/1000:.2f}초"]],[230,269]),Spacer(1,18),p("가상 검사 데이터와 직접 작성한 시연용 표준서로 검증했다. 고정 질문의 검색·인용·원문 일치·필수 구절 포함을 평가한 결과이며 실제 제조 현장의 정확도나 생산성 향상을 측정한 수치는 아니다.",small)]
page(report,"1. 개발 배경과 구현 범위")
report += [p("검사 담당자가 제품별 판정 기록과 재검사 기준을 함께 확인할 수 있도록 검사 이력과 표준서 검색을 결합했다. 제품 선택, 질문, 근거 확인, 재검사 저장을 하나의 화면에서 수행한다."),Image(str(ev/"design-v5-rose.jpg"),width=499,height=346.5,kind="proportional"),Spacer(1,12),table([["요구사항","구현"],["검사 기록 조회","제품 ID 검색, 최초·현재 판정, 관찰과 시각"],["AI 핵심 기능","로컬 LLM의 근거 선택형 RAG"],["자료 연동","VisionQC 내보내기 CSV 가져오기"],["이력 보존","재검사 추가 저장과 최초 기록 보존"]],[125,374])]
page(report,"2. 시스템 설계와 데이터베이스")
report += [p("브라우저는 ASP.NET Core API에 요청한다. API는 SQLite 검사 이력과 표준서 JSON을 조회한다. 관련 문장 후보를 Ollama에 전달하고, 검증한 선택 결과를 원문 인용과 함께 반환한다."),p("활용 기술",h2),table([["영역","기술과 역할"],["서버","C# / .NET 10 / ASP.NET Core"],["화면","HTML, CSS, JavaScript"],["데이터","Microsoft.Data.Sqlite / SQLite"],["AI","Ollama / kanana2:3b"],["문서 검색","키워드 일치와 질문별 문장 우선순위"]],[100,399]),p("데이터 구조",h2),table([["테이블","주요 필드"],["Inspections","Id(PK), Lot, InitialResult, Defect, InspectedAt"],["Reinspections","Sequence(PK), ProductId(FK), RequestId(UNIQUE), Result, Operator, Note, CreatedAt"]],[110,389]),p("재검사는 트랜잭션 안에서 현재 이력 수와 ExpectedRevision을 비교한다. 요청 ID가 같고 내용도 같으면 기존 결과를 반환한다. 다른 제품이나 다른 내용에 같은 ID를 재사용하면 충돌을 반환한다. CSV 가져오기 역시 하나의 트랜잭션으로 처리해 일부 행만 저장되는 상태를 막는다."),p("초기 SQL Server 계획은 설치 없는 제출용 실행을 위해 SQLite로 조정했다. 기존 JSON이 있는 경우 새 DB의 초기 생성 때 이관하고 원본 JSON 파일은 보존한다. 서버 실행 중 DB 파일을 임의로 복사하지 말고 서버 종료 후 data 폴더를 백업한다.",small)]
page(report,"3. 인공지능과 검색 처리")
report += [p("처리 흐름",h2),p("① 제품 ID와 질문을 검증하고 실제 검사 사실을 조회한다.\n② 표준서에서 관련 절 최대 3개를 검색한다.\n③ 질문에 나타난 원인·기록·이력·출하·조건 등의 단어로 문장 후보를 정렬한다.\n④ LLM이 JSON Schema에 따라 근거 문장 번호를 선택한다.\n⑤ 서버가 번호 범위와 개수를 확인하고 원문 및 절 ID를 출력한다."),p("답변 예시",h2),p("질문: 재검사에 필요한 기록은 무엇인가요?\n답변 근거: 재검사 판정, 검사자, 조치 및 확인 메모를 필수로 기록한다. [SOP-03]\n검사 사실: QM-001의 최초 판정, 최신 판정, 재검사 횟수를 별도 표시한다."),p("모델 오류 처리와 개선",h2),p("초기 자유 생성에서는 원문 반복과 인용 누락이 나타났다. JSON 출력만 제한한 중간 평가에서도 질문과 다른 문장을 선택하는 사례가 있어, 문장 우선순위와 필수 답변 구절 검사를 추가했다. 최종 방식은 LLM이 선택한 후보의 실제 원문을 반환한다."),p("관련 절이 없으면 근거 부족 상태를 표시하고 LLM 호출을 생략한다. 모델 연결 실패, 잘못된 JSON, 범위를 벗어난 번호가 발생하면 검색 원문을 표시한다. 이 경로는 AI 답변과 다른 상태로 표시한다."),p("제한 사항",h2),p("표준서가 맞는지, 질문의 모든 요구를 답했는지는 원문 일치만으로 보장할 수 없다. 복합 질문의 완전성, 새로운 표현, 문서 확장에 대한 추가 평가가 필요하다. 현재 답변은 근거 선택형이며 자유로운 장문 요약을 제공하지 않는다.")]
page(report,"4. VisionQC 연동과 검사 데이터")
report += [p("기존 VisionQC.Core/InspectionCsv.cs의 실제 출력 코드를 확인하고 별도 시연 프로그램에서 링크해 가상 검사 3행을 내보냈다. 이 파일의 따옴표와 UTC 시각, OK/NG/ERROR 형식을 가져오기 API에서 처리했다."),table([["CSV 항목","변환 규칙"],["Id + JobId","VQC-작업ID-검사ID로 제품 키 구성"],["OriginalVerdict=OK","PASS · 합격"],["OriginalVerdict=NG","FAIL · 구체 불량 유형 미확인"],["OriginalVerdict=ERROR","ERROR · 검사 오류 유지"],["DefectScore","검사 관찰에 점수 보존"],["InspectedAtUtc","오프셋 시각으로 저장"]],[180,319]),p("중복과 오류 처리",h2),p("동일한 ID와 내용은 중복으로 제외한다. 같은 ID의 검사 내용이 달라지면 전체 가져오기를 거부한다. 헤더 불일치, 잘못된 판정, 중복 ID, 잘못된 시각·점수, 1MB 또는 1,000행 초과 입력을 검사한다."),p("확인한 연동 범위",h2),p("원본 exporter가 만든 시연 파일의 API 가져오기 결과는 3건 추가였다. 브라우저에서 가져온 합격·불합격·검사 오류를 확인했다. 실시간 카메라 연결과 실제 추론 모델의 정확도는 검증하지 않았다. 2026년 11월 20일 브라우저의 파일 선택과 가져오기 버튼으로 원본 샘플을 재선택해 0건 추가·3건 중복 제외를 확인했다."),p("원본 exporter SHA-256\nFB7D220047EF6A2919FB5815D7170C6358D67A65EDC77BB205D59F981066654C",small)]
page(report,"5. 기능 검증")
labels=[("기본 조회","시드 제품 3개 확인"),("재검사 저장","최초 FAIL 보존, 최신 PASS 확인"),("요청 재시도","중복 저장 없이 동일 결과 반환"),("동시 변경","오래된 버전의 저장을 409로 거부"),("입력 검증","빈 검사자·없는 제품·빈 질문 거부"),("검색 예외","무관한 질문에 근거 부족 표시"),("실제 AI","LLM 응답과 관련 절 확인"),("SQLite","DB 생성, 이력 저장, 재시작 보존"),("CSV 연동","NG·ERROR 처리, 중복 제외, 충돌·오류 거부")]
report += [p("별도 임시 데이터 디렉터리와 포트로 검증했다. 실제 시연용 데이터와 테스트 데이터를 분리했다. tools/check.ps1의 17개 검사가 최종 코드에서 통과했다."),table([["검사 영역","확인 내용"],*labels],[110,389]),p("브라우저 검증",h2),p("실제 화면에서 재검사를 저장하고 최초 불합격과 현재 합격을 함께 확인했다. 로컬 AI의 SOP-03 인용 답변을 확인했다. 데스크톱 1440×1000과 모바일 390×844에서 화면을 확인했으며 모바일의 가로 넘침은 관찰되지 않았다."),p("빌드와 배포",h2),p(".NET 빌드가 경고 0개와 오류 0개로 통과했다. Windows x64 자체 포함 실행 패키지를 생성하고, 별도 포트와 데이터 폴더에서 EXE 직접 실행, 화면 응답, 실제 AI 답변, CSV 3건 저장을 확인했다. 다른 PC의 실제 실행은 아직 확인하지 않았으며, AI 사용에는 Ollama와 해당 모델 준비가 필요하다.")]
page(report,"6. RAG 고정 질문 평가")
rows=[["번호","질문","결과"]]+[[r["id"],r["question"],"통과" if r["pass"] else "실패"] for r in results]
report += [p(f"도메인 질문 16개와 무관한 질문 4개, 총 20개를 평가해 {summary['passed']}개가 기준을 통과했다.",small),table(rows,[35,420,44]),Spacer(1,9),p(f"LLM 호출 16개 중앙값 {summary['llmMedianMs']/1000:.2f}초. 전체 20개 중앙값 {summary['medianMs']/1000:.2f}초, 최대 {summary['maxMs']/1000:.2f}초. 같은 PC의 로컬 측정이며 워밍업과 동시 작업의 영향을 받는다.",small),p("통과 기준은 관련 절 검색, 실제 LLM 동작, 인용 표시, 출처 원문 일치, 질문별 필수 구절 포함이다. 무관한 질문은 근거 부족 응답을 확인했다. 전체적인 의미의 정확도나 미지의 질문 성능을 보장하지 않는다.",small)]
page(report,"7. 실행 시연과 향후 개선")
report += [p("시연 순서",h2),p("① 실행하기.cmd로 서버와 브라우저를 연다.\n② AI 연결 상태와 기본 제품 3개를 확인한다.\n③ QM-001을 선택하고 재검사 기록에 필요한 항목을 질문한다.\n④ 답변의 SOP-03과 표준서 원문을 확인한다.\n⑤ 판정·검사자·메모를 입력하고 재검사를 저장한다.\n⑥ 최초 FAIL과 현재 PASS가 함께 남는 것을 확인한다.\n⑦ 샘플 CSV를 가져와 OK·NG·ERROR를 확인한다.\n⑧ 같은 파일을 다시 가져와 중복 제외를 확인한다."),p("개발 성과",h2),p("검사 데이터와 문서 근거를 하나의 화면에서 연결했다. 재시도와 동시 변경, CSV 충돌에서 이력의 정합성을 지키도록 구현했다. 근거 없는 자유 생성의 범위를 줄이고 실제 고정 질문 평가 결과를 확보했다."),p("남은 한계",h2),p("실제 품질 기준과 현장 데이터, 카메라 추론, 사용자 권한, 문서 업로드, 대규모 검색 성능은 검증 범위 밖이다. 로컬 시연을 넘어 다수 사용자에게 공개하려면 인증·권한과 운영 환경을 추가 설계해야 한다."),p("향후 개선",h2),p("실제 표준서와 현장 담당자가 검토한 질문으로 평가를 확장한다. 복합 질문에 필요한 근거의 누락을 측정한다. 문서 수가 늘면 임베딩 검색과 재순위화를 비교하고, 실제 검사 결과 수신을 자동화한다."),p("자료와 재현",h2),p("전체 소스, Windows 실행 패키지, 샘플 CSV, 표준서, 기능 검사 로그, 질문별 평가 JSON을 함께 제출한다. AI 가중치는 포함하지 않으며 실행 안내에 모델 준비 절차를 기재한다. 개발 과정에서 AI 코딩 도구를 활용했으며 제출자는 코드와 역할 설명을 직접 확인해야 한다.",small)]
write("QualityMate_결과보고서.pdf",report)
plan=[]
plan += [p("QualityMate 프로젝트 기획서",h1),p("검사 이력과 작업표준서를 연계한 RAG 기반 품질관리 지원 시스템",h2),p("기획서 제출일 2026년 12월 4일\n최종 발표일 2026년 12월 30일\n작성 기준 2026년 11월 20일 · 개인 프로젝트"),p("목표와 대상",h2),p("검사 담당자가 제품 판정 이력과 관련 재검사 절차를 함께 확인할 수 있도록 한다. C# 서버와 관계형 데이터베이스, 로컬 LLM 기반 RAG를 결합한 실행 가능한 시스템을 개발한다."),p("요구사항",h2),table([["구분","주요 기능"],["검사 데이터","제품 검색, 최초·현재 판정과 재검사 이력 조회"],["AI 핵심","질문 관련 표준서 검색과 LLM 근거 선택"],["데이터 연동","VisionQC 원본 결과 CSV 가져오기"],["정합성","재검사 중복 방지, 버전 충돌, CSV 원자적 저장"],["발표","PPT와 실행 시연, 검증 결과 및 한계 설명"]],[100,399]),p("구현 범위",h2),p("가상 제품 한 종류의 검사 업무를 대상으로 표면 흠집과 이물 의심, 시연용 표준서 4개 절을 사용한다. 실제 공장 적용과 카메라 추론 정확도는 별도 검증이 필요하다.")]
page(plan,"설계와 수행 일정")
plan += [p("시스템 구성",h2),p("브라우저에서 C# ASP.NET Core API를 호출하고, API가 SQLite 검사 이력과 표준서 JSON을 조회한다. 로컬 Ollama의 kanana2:3b가 관련 근거 문장을 선택한다. 서버는 선택을 검증하고 원문과 절 ID를 표시한다."),p("기술 선택",h2),p("C#/.NET 10, HTML/CSS/JavaScript, SQLite, Ollama를 사용한다. 초기 SQL Server 계획은 설치와 시연의 재현성을 고려해 SQLite로 조정했다. 모든 학습 기술을 추가하기보다 검사 이력과 챗봇의 통합에 집중한다."),table([["시기","수행 내용"],["11.19 기준","통합 실행 버전과 고정 질문 평가 확보"],["12.04","기획서 제출과 요구사항 최종 확인"],["12.05~12.16","발표자 환경에서 실행·데이터 점검과 추가 질문 검토"],["12.17~12.23","현장 용어 검토, 시연 리허설과 오류 보완"],["12.24~12.29","최종 제출 파일과 발표 환경 확인"],["12.30","PPT 발표, 시스템 시연, 질의응답"]],[100,399]),p("역할과 제출물",h2),p("개인 프로젝트로 수행한다. 기획·서버·화면·RAG·평가를 통합하며, AI 코딩 도구 활용 내역과 실제 수행 내용은 발표자가 확인한다. 기획서, 결과보고서, PPT, 소스 코드, 실행 패키지를 제출한다."),p("검증과 완료 기준",h2),p("검사 조회, 재검사 정합성, CSV 연동, 실제 LLM 답변, 근거 없는 질문 처리를 검증한다. 고정 질문 20개로 관련 근거와 인용, 원문 일치, 필요한 답변 구절을 확인한다. 실제 사용자와 생산 현장의 성능은 별도 검증 대상으로 둔다.")]
write("QualityMate_기획서.pdf",plan)
# Editable text companions preserve content for revision.
def plain(item):
    return html.unescape(re.sub(r"<[^>]+>", "", item.text.replace("<br/>", "\n")))
for filename,story in [("QualityMate_결과보고서.md",report),("QualityMate_기획서.md",plan)]:
    lines=[]
    for item in story:
        if isinstance(item,Paragraph):
            prefix = "# " if item.style.name == "title" else "## " if item.style.name == "h1" else "### " if item.style.name == "h2" else ""
            lines.append(prefix + plain(item))
        elif isinstance(item,Table):
            for index,row in enumerate(item._cellvalues):
                lines.append("| " + " | ".join(plain(v) if isinstance(v,Paragraph) else str(v) for v in row) + " |")
                if index == 0: lines.append("| " + " | ".join("---" for _ in row) + " |")
        elif isinstance(item,PageBreak): lines.append("\n---\n")
    (out/filename).write_text("\n\n".join(lines),encoding="utf-8")
print("Created report and plan",summary)






