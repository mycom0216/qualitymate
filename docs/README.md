# QualityMate 문서 묶음

| 문서 | 파일 |
|---|---|
| 기획서 | `QualityMate_기획서` (.md · .docx · .pdf) |
| 요구사항 분석서 | `QualityMate_요구사항분석서` |
| 요구사항 명세서 | `QualityMate_요구사항명세서` |
| 데이터정의서 (ERD 포함) | `QualityMate_데이터정의서`, `images/erd.png` |
| 순서도 4종 (개발 절차 · 챗봇 · 재검사 저장 · CSV 가져오기) | `QualityMate_순서도`, `images/flow-*.png` |
| 개발일정표 | `QualityMate_개발일정표` |
| 포트폴리오 | `QualityMate_포트폴리오` |
| 발표자료 | `QualityMate_발표자료.pptx` · `.pdf` (기존 12장 + ERD·순서도·오답 수정 5장 = 17장) |
| 코드 분석(11/21) | `코드분석-2026-11-21.md` |

결과보고서와 기존 최종 발표는 `../submission/documents/`에 있습니다(2026-11-20 기준 기록).

## 다시 만들기
```powershell
python tools/docs/make_diagrams.py     # 순서도·ERD PNG
# md → docx: python tools/docs/md2docx.py docs/QualityMate_기획서.md docs/QualityMate_기획서.docx  (각 문서)
powershell -ExecutionPolicy Bypass -File tools/docs/docx2pdf.ps1 docs/QualityMate_기획서.docx          # docx → pdf (Word 필요)
python tools/docs/build_deck.py        # 발표자료 보강
powershell -ExecutionPolicy Bypass -File tools/docs/pptx2pdf.ps1 docs/QualityMate_발표자료.pptx       # pptx → pdf (PowerPoint 필요)
```
