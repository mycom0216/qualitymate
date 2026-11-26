"""QualityMate 다이어그램 생성: python tools/docs/make_diagrams.py
  → docs/images/flow-dev.png        개발 절차 순서도
  → docs/images/flow-chat.png       챗봇(/api/chat) 처리 순서도
  → docs/images/flow-reinspect.png  재검사 저장 순서도(중복 요청·버전 충돌)
  → docs/images/flow-import.png     VisionQC CSV 가져오기 순서도(전체 성공 또는 전체 취소)
  → docs/images/erd.png             ERD(SQLite 2개 테이블 + 표준서 JSON)
표준 순서도 기호: 둥근 사각형=시작/끝, 사각형=처리, 마름모=판단, 평행사변형=입출력.
"""
from pathlib import Path
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, Polygon

plt.rcParams["font.family"] = "Malgun Gothic"
OUT = Path(__file__).resolve().parent.parent.parent / "docs" / "images"
OUT.mkdir(parents=True, exist_ok=True)
BRAND, GREEN, RED, ORANGE, INK = "#be185d", "#0f9d6b", "#d92d20", "#d97706", "#1b2430"


class Flow:
    def __init__(self, w, h, y0=-1.2):
        self.fig, self.ax = plt.subplots(figsize=(w, h - y0 if y0 < 0 else h - y0))
        self.ax.set_xlim(-1.2, w + 0.8)
        self.ax.set_ylim(y0, h)
        self.ax.axis("off")
        self.nodes = {}

    def node(self, key, x, y, text, kind="proc", w=3.4, h=0.8):
        a = self.ax
        if kind == "term":
            a.add_patch(FancyBboxPatch((x - w / 2, y - h / 2), w, h, boxstyle="round,pad=0,rounding_size=0.3", fc=BRAND, ec=BRAND, zorder=3))
            col = "white"
        elif kind == "dec":
            hw, hh = w / 2 + 0.2, h / 2 + 0.25
            a.add_patch(Polygon([(x, y + hh), (x + hw, y), (x, y - hh), (x - hw, y)], fc="#fff7e6", ec=ORANGE, lw=1.8, zorder=3))
            col = INK
        elif kind == "io":
            s = 0.3
            a.add_patch(Polygon([(x - w / 2 + s, y + h / 2), (x + w / 2 + s, y + h / 2), (x + w / 2 - s, y - h / 2), (x - w / 2 - s, y - h / 2)], fc="#fce7f3", ec=BRAND, lw=1.8, zorder=3))
            col = INK
        else:
            a.add_patch(FancyBboxPatch((x - w / 2, y - h / 2), w, h, boxstyle="round,pad=0,rounding_size=0.08", fc="white", ec=BRAND, lw=1.8, zorder=3))
            col = INK
        a.text(x, y, text, ha="center", va="center", fontsize=11, color=col, zorder=5, fontweight="bold" if kind == "term" else "normal")
        hh = (h / 2 + 0.25) if kind == "dec" else h / 2
        hw = (w / 2 + 0.2) if kind == "dec" else w / 2
        self.nodes[key] = (x, y, hw, hh)

    def pt(self, key, side):
        x, y, hw, hh = self.nodes[key]
        return {"t": (x, y + hh), "b": (x, y - hh), "l": (x - hw, y), "r": (x + hw, y)}[side]

    def arrow(self, a, sa, b, sb, label="", via=None, color="#475467"):
        p1, p2 = self.pt(a, sa), self.pt(b, sb)
        pts = [p1] + (via or []) + [p2]
        for i in range(len(pts) - 1):
            last = i == len(pts) - 2
            self.ax.annotate("", xy=pts[i + 1], xytext=pts[i], zorder=2,
                             arrowprops=dict(arrowstyle="-|>" if last else "-", color=color, lw=1.5, shrinkA=0, shrinkB=0))
        if label:
            mx, my = pts[0][0] + (pts[1][0] - pts[0][0]) * 0.35, pts[0][1] + (pts[1][1] - pts[0][1]) * 0.35
            self.ax.text(mx + 0.12, my + 0.08, label, fontsize=10, color=RED if label.startswith("아니") else GREEN, fontweight="bold", zorder=6,
                         bbox=dict(fc="white", ec="none", pad=1))

    def save(self, name, title):
        self.ax.text(-1.0, self.ax.get_ylim()[1] - 0.35, title, fontsize=16, fontweight="bold", color=INK)
        self.fig.savefig(OUT / name, dpi=110, bbox_inches="tight", facecolor="white")
        # 아래·위에 남는 흰 여백을 잘라 내 문서·슬라이드에서 그림이 최대한 크게 보이게 합니다.
        from PIL import Image, ImageChops
        im = Image.open(OUT / name).convert("RGB")
        box = ImageChops.difference(im, Image.new("RGB", im.size, "white")).getbbox()
        if box:
            pad = 20
            im.crop((max(0, box[0] - pad), max(0, box[1] - pad), min(im.width, box[2] + pad), min(im.height, box[3] + pad))).save(OUT / name)
        print("saved", OUT / name)


# ── 1) 개발 절차 순서도 ──────────────────────────────────────────────
f = Flow(9, 12.4)
f.node("s", 4.5, 11.3, "시작 (2026-11-19)", "term", 3.0, 0.7)
f.node("p1", 4.5, 10.1, "1 기획\n주제·범위 확정, 기획서 (제출 12/04)", "proc", 5.4)
f.node("p2", 4.5, 8.8, "2 설계\nASP.NET Core API, SQLite, 표준서 JSON, Ollama", "proc", 5.6)
f.node("p3", 4.5, 7.5, "3 구현\n검사 이력·재검사·CSV 가져오기·근거 선택 챗봇", "proc", 5.6)
f.node("p4", 4.5, 6.2, "4 테스트\n기능 17개, 고정 질문 20개, 회귀 56개", "proc", 5.4)
f.node("d1", 4.5, 4.7, "결함·개선 사항이\n남아 있는가?", "dec", 2.6, 0.7)
f.node("p5", 4.5, 3.2, "5 유지보수\n오답 수정(11/21), 문서·코드 분석 갱신", "proc", 5.4)
f.node("p6", 4.5, 1.95, "발표자료 정리, 시연 리허설", "proc", 4.4)
f.node("e", 4.5, 0.85, "최종 발표 (2026-12-30)", "term", 3.2, 0.7)
for a, b in [("s", "p1"), ("p1", "p2"), ("p2", "p3"), ("p3", "p4"), ("p4", "d1")]:
    f.arrow(a, "b", b, "t")
f.arrow("d1", "b", "p5", "t", "아니오")
f.arrow("d1", "r", "p3", "r", "예 → 구현으로 수정", via=[(7.9, 4.7), (7.9, 7.5)], color=RED)
f.arrow("p5", "b", "p6", "t"); f.arrow("p6", "b", "e", "t")
f.save("flow-dev.png", "개발 절차 순서도 (기획 → 설계 → 구현 → 테스트 → 유지보수)")

# ── 2) 챗봇 처리 순서도(POST /api/chat) ────────────────────────────────
c = Flow(11.4, 15.2, y0=-1.4)
LX, RX = 2.5, 8.0
c.node("s", LX, 13.9, "시작: 제품 선택 + 질문 입력", "term", 3.8, 0.6)
c.node("v1", LX, 12.6, "질문이 1~2,000자인가?", "dec", 2.6, 0.6)
c.node("v2", LX, 11.2, "선택한 제품의\n검사 기록이 있는가?", "dec", 2.6, 0.65)
c.node("pl", LX, 9.8, "질문 분류(KnowledgeBase.Plan)\n지원 범위·질문 의도 판단", "proc", 3.9, 0.8)
c.node("d1", LX, 8.3, "표준서에 없는\n주제인가?(가격·날씨 등)", "dec", 2.8, 0.7)
c.node("d2", LX, 6.8, "제품 기록 질문인가?\n(판정·재검사 횟수·LOT)", "dec", 2.8, 0.7)
c.node("d3", LX, 5.3, "관련 원문 후보가\n있는가?", "dec", 2.6, 0.65)
c.node("ol", LX, 3.7, "후보 문장에 번호(0~)를 붙여\nOllama 호출(JSON 스키마, 최대 4개)", "proc", 4.2, 0.85)
c.node("d4", LX, 2.1, "응답이 올바른가?\n(통신·JSON·번호 범위)", "dec", 2.6, 0.65)
c.node("d5", LX, 0.6, "선택한 번호가 있는가?", "dec", 2.6, 0.6)
c.node("e400", RX, 12.6, "400 질문 오류", "proc", 2.6, 0.55)
c.node("e404", RX, 11.2, "404 기록 없음", "proc", 2.6, 0.55)
c.node("noe", RX, 8.3, "no-evidence\n모델 호출 없이 근거 부족 안내", "proc", 3.8, 0.8)
c.node("rec", RX, 6.8, "record\nDB 값으로 직접 답변", "proc", 3.4, 0.75)
c.node("rt", RX, 2.1, "retrieval-only\n검색 원문 표시(AI 연결 필요)", "proc", 3.8, 0.8)
c.node("so", RX, 0.6, "source-only\n원문 표시 + 'AI 선택 없음' 안내", "proc", 3.8, 0.8)
c.node("llm", LX, -0.9, "llm: 선택 번호의 원문 + 절 ID 표시", "proc", 4.4, 0.6)
c.node("noe2", RX, 5.3, "no-evidence\n근거 부족 안내", "proc", 3.0, 0.75)
c.arrow("s", "b", "v1", "t"); c.arrow("v1", "b", "v2", "t", "예"); c.arrow("v1", "r", "e400", "l", "아니오")
c.arrow("v2", "b", "pl", "t", "예"); c.arrow("v2", "r", "e404", "l", "아니오")
c.arrow("pl", "b", "d1", "t"); c.arrow("d1", "r", "noe", "l", "예"); c.arrow("d1", "b", "d2", "t", "아니오")
c.arrow("d2", "r", "rec", "l", "예"); c.arrow("d2", "b", "d3", "t", "아니오")
c.arrow("d3", "r", "noe2", "l", "아니오")
c.arrow("d3", "b", "ol", "t", "예"); c.arrow("ol", "b", "d4", "t")
c.arrow("d4", "r", "rt", "l", "아니오"); c.arrow("d4", "b", "d5", "t", "예")
c.arrow("d5", "r", "so", "l", "아니오"); c.arrow("d5", "b", "llm", "t", "예")
c.save("flow-chat.png", "챗봇 처리 순서도 (POST /api/chat)")

# ── 3) 재검사 저장 순서도 ──────────────────────────────────────────────
r = Flow(10, 14.2, y0=-1.2)
r.node("s", 3.6, 12.9, "시작: 재검사 저장 요청\n(판정·검사자·메모·expectedRevision·requestId)", "term", 5.2, 0.8)
r.node("v", 3.6, 11.4, "입력이 올바른가?\n(PASS/FAIL, 검사자 ≤50자,\n메모 ≤1,000자, 요청 ID)", "dec", 3.0, 0.85)
r.node("tx", 3.6, 9.6, "쓰기 트랜잭션 시작\n제품 조회", "proc", 3.2, 0.75)
r.node("d1", 3.6, 8.1, "제품이 있는가?", "dec", 2.2, 0.6)
r.node("d2", 3.6, 6.7, "같은 요청 ID가\n이미 저장돼 있는가?", "dec", 2.8, 0.65)
r.node("d3", 3.6, 5.0, "expectedRevision =\n현재 재검사 횟수?", "dec", 2.8, 0.7)
r.node("ins", 3.6, 3.4, "재검사 행 추가(INSERT)\n최초 기록은 수정하지 않음", "proc", 3.8, 0.8)
r.node("ok", 3.6, 1.9, "커밋 → 200 최신 상태 반환", "proc", 3.6, 0.65)
r.node("e", 3.6, 0.7, "끝", "term", 1.6, 0.55)
r.node("b400", 8.3, 11.4, "400 입력 오류", "proc", 2.2, 0.55)
r.node("b404", 8.3, 8.1, "404 제품 없음", "proc", 2.2, 0.55)
r.node("same", 8.3, 6.7, "같은 제품·같은 내용?", "dec", 2.6, 0.6)
r.node("ret", 8.3, 5.2, "200 기존 결과 반환\n(재시도라 중복 저장 안 함)", "proc", 3.0, 0.8)
r.node("c409a", 8.3, 3.9, "409 같은 ID에\n다른 내용", "proc", 2.4, 0.75)
r.node("c409b", 8.3, 2.5, "409 다른 검사자가\n먼저 변경(새로고침)", "proc", 2.9, 0.75)
r.arrow("s", "b", "v", "t"); r.arrow("v", "r", "b400", "l", "아니오"); r.arrow("v", "b", "tx", "t", "예")
r.arrow("tx", "b", "d1", "t"); r.arrow("d1", "r", "b404", "l", "아니오"); r.arrow("d1", "b", "d2", "t", "예")
r.arrow("d2", "r", "same", "l", "예"); r.arrow("d2", "b", "d3", "t", "아니오")
r.arrow("same", "b", "ret", "t", "예"); r.arrow("same", "r", "c409a", "r", "아니오", via=[(10.6, 6.7), (10.6, 3.9)])
r.arrow("d3", "b", "ins", "t", "예"); r.arrow("d3", "r", "c409b", "l", "아니오", via=[(6.35, 5.0), (6.35, 2.5)])
r.arrow("ins", "b", "ok", "t"); r.arrow("ok", "b", "e", "t")
r.save("flow-reinspect.png", "재검사 저장 순서도 (중복 요청 · 버전 충돌)")

# ── 4) CSV 가져오기 순서도 ─────────────────────────────────────────────
m = Flow(11, 13.2, y0=-1.2)
m.node("s", 3.6, 11.9, "시작: VisionQC CSV 가져오기", "term", 4.2, 0.6)
m.node("p1", 3.6, 10.7, "CSV 전체를 먼저 읽어 검증\n(1MB 이하, 헤더 8개 일치)", "proc", 4.0, 0.8)
m.node("d1", 3.6, 9.2, "모든 행이 올바른가?\n(ID·시각·OK/NG/ERROR·점수·\n파일 안 중복 ID·1,000행 이하)", "dec", 3.3, 0.85)
m.node("map", 3.6, 7.4, "행 변환: OK→PASS, NG→FAIL(유형 미확인),\nERROR→ERROR 유지, 제품 키 VQC-작업-검사", "proc", 5.2, 0.85)
m.node("tx", 3.6, 5.9, "쓰기 트랜잭션 시작\n기존 검사 목록 조회", "proc", 3.4, 0.75)
m.node("d2", 3.6, 4.4, "행의 ID가 이미 있는가?", "dec", 2.6, 0.6)
m.node("d3", 3.6, 2.9, "기존 내용과 같은가?", "dec", 2.4, 0.6)
m.node("rb", 3.6, 1.3, "409 기존 내용과 다름\n전체 롤백(아무것도 저장 안 함)", "proc", 4.2, 0.8)
m.node("b400", 8.6, 9.2, "400 형식 오류\n(아무것도 저장 안 함)", "proc", 2.9, 0.8)
m.node("ins", 8.6, 4.4, "새 검사 INSERT\n(added 증가)", "proc", 2.6, 0.75)
m.node("skip", 8.6, 2.9, "건너뜀(중복)", "proc", 2.4, 0.6)
m.node("nx", 8.6, 1.3, "나머지 행 반복 → 모두 끝나면\n커밋 → 200 {added, skipped}", "proc", 3.7, 0.8)
m.arrow("s", "b", "p1", "t"); m.arrow("p1", "b", "d1", "t")
m.arrow("d1", "r", "b400", "l", "아니오"); m.arrow("d1", "b", "map", "t", "예")
m.arrow("map", "b", "tx", "t"); m.arrow("tx", "b", "d2", "t")
m.arrow("d2", "r", "ins", "l", "아니오"); m.arrow("d2", "b", "d3", "t", "예")
m.arrow("d3", "r", "skip", "l", "예"); m.arrow("d3", "b", "rb", "t", "아니오")
m.arrow("ins", "r", "nx", "r", via=[(10.6, 4.4), (10.6, 1.3)]); m.arrow("skip", "b", "nx", "t")
m.save("flow-import.png", "VisionQC CSV 가져오기 순서도 (전체 성공 또는 전체 취소)")

# ── 5) ERD ──────────────────────────────────────────────────────────
from matplotlib.patches import FancyBboxPatch
fig, ax = plt.subplots(figsize=(15, 8))
ax.set_xlim(0, 15); ax.set_ylim(0, 8.2); ax.axis("off")
ax.text(0.4, 7.7, "QualityMate ERD (SQLite 2개 테이블 + 표준서 JSON 파일)", fontsize=19, fontweight="bold", color=INK)
ax.text(0.4, 7.25, "1개 제품(Inspections) → 여러 재검사(Reinspections). 재검사는 추가만 하고 최초 검사 행은 수정하지 않음. 최종 판정·횟수는 계산값(Revision = 재검사 수).", fontsize=10.5, color="#667085")

def table(x, y, name, rows, color=BRAND, w=5.2, dashed=False):
    h = 0.6 + 0.42 * len(rows)
    ax.add_patch(FancyBboxPatch((x, y - h), w, h, boxstyle="round,pad=0.02,rounding_size=0.12", fc="white", ec=color, lw=2, ls="--" if dashed else "-", zorder=3))
    ax.add_patch(FancyBboxPatch((x, y - 0.6), w, 0.6, boxstyle="round,pad=0.02,rounding_size=0.12", fc=color, ec=color, zorder=4))
    ax.text(x + w / 2, y - 0.3, name, color="white", fontsize=13, fontweight="bold", ha="center", va="center", zorder=5)
    for i, (k, c) in enumerate(rows):
        yy = y - 0.6 - 0.42 * (i + 0.5)
        if k: ax.text(x + 0.15, yy, k, fontsize=9, color="#b42318" if "PK" in k else "#175cd3", fontweight="bold", va="center", zorder=5)
        ax.text(x + 1.05, yy, c, fontsize=10.5, color=INK, va="center", zorder=5)
    return h

table(0.5, 6.4, "Inspections (최초 검사)", [("PK", "Id  (예: QM-001)"), ("", "Lot"), ("", "InitialResult  CHECK PASS|FAIL|ERROR"), ("", "Defect  (관찰 내용)"), ("", "InspectedAt  (ISO 8601)")])
table(8.4, 6.4, "Reinspections (재검사 이력)", [("PK", "Sequence  (자동 증가)"), ("FK", "ProductId → Inspections.Id"), ("UQ", "RequestId  (UNIQUE, 중복 저장 방지)"), ("", "Result  CHECK PASS|FAIL"), ("", "Operator  (≤50자)"), ("", "Note  (≤1,000자)"), ("", "CreatedAt  (UTC)")], w=6.1)
table(0.5, 2.9, "standards.json (표준서, DB 아님)", [("", "id  (SOP-01 ~ SOP-04)"), ("", "title · version"), ("", "text  (원문, 문장 단위로 인용)"), ("", "keywords  (호환용, 현재 점수에 미사용)")], color="#667085", dashed=True)
ax.annotate("", xy=(8.4, 4.6), xytext=(5.7, 4.6), arrowprops=dict(arrowstyle="-|>", color="#475467", lw=1.6))
ax.text(7.05, 4.78, "1 : N", fontsize=11, color="#d92d20", fontweight="bold", ha="center", bbox=dict(fc="white", ec="none", pad=1))
ax.text(8.4, 2.1, "인덱스 IX_Reinspections_Product(ProductId, Sequence)\nPRAGMA foreign_keys=ON · journal_mode=WAL", fontsize=10.5, color="#475467")
ax.text(8.4, 1.2, "Reinspections.RequestId가 같고 내용도 같으면 재시도로 보고 기존 결과를 돌려줌", fontsize=10, color="#475467")
fig.savefig(OUT / "erd.png", dpi=110, bbox_inches="tight", facecolor="white")
print("saved", OUT / "erd.png")
