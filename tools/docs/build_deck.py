"""발표자료 보강: python tools/docs/build_deck.py
기존 최종 발표(submission/documents/QualityMate_최종발표.pptx, 12장)에 빠져 있던 그림과 최신 결과를 넣어 docs/QualityMate_발표자료.pptx 를 만듭니다.
  · ERD(데이터 정합성 슬라이드 다음), 재검사 저장 순서도
  · 챗봇 처리 순서도(근거 선택형 RAG 슬라이드 다음)
  · VisionQC CSV 가져오기 순서도(파일 연동 슬라이드 다음)
  · 2026-11-21 오답 수정과 회귀 결과(검증 결과 슬라이드 다음)
새 슬라이드는 기존 슬라이드의 제목·본문 도형을 복사해 같은 모양으로 만듭니다(글꼴·색 그대로).
"""
import copy
from pathlib import Path

from pptx import Presentation
from pptx.util import Inches

ROOT = Path(__file__).resolve().parent.parent.parent          # QualityMate/
SRC = ROOT / "submission" / "documents" / "QualityMate_최종발표.pptx"
IMG = ROOT / "docs" / "images"
OUT = ROOT / "docs" / "QualityMate_발표자료.pptx"

prs = Presentation(str(SRC))
orig_slides = list(prs.slides)
ref = orig_slides[3]  # '시스템 아키텍처' 슬라이드: 제목·본문 모양의 견본
title_src = ref.shapes[0]
note_src = ref.shapes[-1]  # 아래쪽 설명 문장 상자


def clone(slide, src, text, left=None, top=None, width=None, height=None):
    """견본 도형을 새 슬라이드에 복사하고 글자만 바꿉니다."""
    el = copy.deepcopy(src._element)
    slide.shapes._spTree.append(el)
    shp = slide.shapes[-1]
    paras = shp.text_frame.paragraphs
    paras[0].runs[0].text = text
    for r in paras[0].runs[1:]:
        r.text = ""
    for extra in paras[1:]:
        extra._p.getparent().remove(extra._p)
    if left is not None:
        shp.left, shp.top, shp.width, shp.height = left, top, width, height
    return shp


def new_slide(title, image, bullets):
    s = prs.slides.add_slide(prs.slide_layouts[5])  # 'Blank'
    # 배경(연한 분홍)은 슬라이드마다 따로 지정돼 있으므로 견본 슬라이드의 배경을 복사합니다.
    bg = ref._element.cSld.find('{http://schemas.openxmlformats.org/presentationml/2006/main}bg')
    if bg is not None:
        s._element.cSld.insert(0, copy.deepcopy(bg))
    clone(s, title_src, title)
    if image:
        pic = s.shapes.add_picture(str(IMG / image), Inches(0.6), Inches(1.35), height=Inches(5.45))
        if pic.width > Inches(7.6):                      # 가로로 넓은 그림은 폭에 맞춤
            ratio = Inches(7.6) / pic.width
            pic.width, pic.height = int(pic.width * ratio), int(pic.height * ratio)
        left, width = pic.left + pic.width + Inches(0.35), prs.slide_width - (pic.left + pic.width + Inches(0.35)) - Inches(0.4)
    else:
        left, width = Inches(0.8), prs.slide_width - Inches(1.6)
    for i, text in enumerate(bullets):
        clone(s, note_src, text, left, Inches(1.6) + i * Inches(1.15), width, Inches(1.0))
    return s


erd = new_slide("ERD: 테이블 2개 + 표준서 JSON", "erd.png", [
    "Inspections 1 : N Reinspections",
    "최초 검사는 수정하지 않고 재검사만 추가",
    "RequestId UNIQUE로 중복 저장 방지",
])
reins = new_slide("재검사 저장 순서도", "flow-reinspect.png", [
    "한 트랜잭션에서 비교와 삽입",
    "같은 요청 ID·같은 내용 = 재시도(기존 결과 반환)",
    "같은 ID 다른 내용, 오래된 버전 = 409",
])
chat = new_slide("챗봇 처리 순서도", "flow-chat.png", [
    "범위 밖 질문은 모델 호출 전에 거절",
    "LLM은 번호만 선택, 서버가 검증 후 원문 출력",
    "응답 모드: llm · source-only · retrieval-only · no-evidence · record",
])
csv = new_slide("VisionQC CSV 가져오기 순서도", "flow-import.png", [
    "전체를 먼저 검증, 하나라도 오류면 저장 안 함",
    "같은 내용은 건너뜀, 다른 내용은 전체 롤백",
    "ERROR는 검사 오류로 유지(불량으로 추정 안 함)",
])
fix = new_slide("2026-11-21 오답 수정과 회귀 결과", None, [
    "오류: \"제품 가격은 얼마인가요?\"에 출하 보류 문장이 답으로 나옴(단어 하나 겹침 + 최소 1개 선택 스키마)",
    "수정: 표준서에 없는 질문은 모델 호출 전에 거절, 의도에 맞는 문장만 후보로",
    "결과: 실제 모델 회귀 56/56, 모의 모델 계약 11/11. 실제 모델 20문항 반복에서 llm 12 · source-only 8",
    "source-only는 AI가 선택하지 않은 경로라 AI 정확도로 세지 않음",
])

# 순서: 원본 1~5, ERD, 재검사, 원본 6, 챗봇, 원본 7~8, CSV, 원본 9~10, 오답 수정, 원본 11~12
lst = prs.slides._sldIdLst
ids = list(lst)
o = ids[: len(orig_slides)]
e, r, c, v, f = ids[len(orig_slides):]
order = o[:5] + [e, r] + o[5:6] + [c] + o[6:8] + [v] + o[8:10] + [f] + o[10:]
for el in ids:
    lst.remove(el)
for el in order:
    lst.append(el)

prs.save(str(OUT))
print("saved", OUT, "slides:", len(prs.slides))
