# QualityMate
검사 이력과 작업표준서를 연계한 근거 선택형 RAG 품질관리 시스템입니다.

## 실행
.NET 10 SDK로 이 폴더에서 dotnet run을 실행하거나 시작하기.cmd를 실행합니다.
주소는 http://127.0.0.1:5187 입니다.
제출용 자체 포함 실행 파일은 submission/runtime에 있습니다.

## 구현
C# ASP.NET Core, SQLite, HTML/CSS/JavaScript와 Ollama를 사용합니다.
검사 조회, 재검사 저장, 중복·버전 충돌 처리, VisionQC CSV 가져오기, 표준서 검색과 LLM 근거 선택을 제공합니다.
현재 표준서는 knowledge/standards.json의 가상 문서 4개 절입니다.
제품 번호로 실제 검사 사실을 읽고 질문 관련 후보 문장을 LLM에 제공합니다.
모델이 JSON Schema로 선택한 번호를 검증한 뒤 해당 원문과 절 ID를 표시합니다.
연결 오류 시 검색 원문으로 전환하며 AI 연결 필요 상태를 표시합니다.
표현력이 제한되는 근거 선택형 답변이며 실제 현장 정확도는 검증하지 않았습니다.

## 저장과 연동
data/qualitymate.db에 저장합니다. 최초 판정과 재검사를 분리하고 트랜잭션·요청 ID·이력 버전으로 정합성을 유지합니다.
기존 JSON 파일이 있으면 새 DB 초기 생성 때 이관하고 원본을 보존합니다.
VisionQC.Core/InspectionCsv.cs 형식의 CSV를 읽습니다. 샘플은 해당 실제 exporter가 가상 행을 내보낸 결과입니다.
ERROR는 검사 오류로 유지합니다. 중복 파일은 제외하고 내용 충돌은 전체 롤백합니다.

## API
GET /api/inspections
GET /api/documents
GET /api/status
POST /api/chat — productId, question
POST /api/import/visionqc — csv
POST /api/inspections/{id}/reinspections — result, operator, note, expectedRevision, requestId

## 검사
PowerShell 7에서 ./tools/check.ps1을 실행합니다. 별도 데이터로 검사합니다.
서버 실행 후 ./tools/evaluate.ps1을 실행하면 고정 질문 20개의 결과와 시간을 submission/evidence에 저장합니다.
최종 측정은 기능 17개 통과, RAG 고정 질문 20개 통과, LLM 호출 16개 중앙값 2.98초입니다.
고정 세트의 검색·인용·원문 일치·필수 구절 검사이며 일반적인 현장 정확도를 뜻하지 않습니다.

## 제출 자료
submission/제출안내.md에 실행, 모델 준비, 제출 파일과 한계를 정리했습니다.
최종 기획서·결과보고서·PPT는 submission/documents에 있습니다.
초기 기획서-초안.md는 첫 버전의 계획이며 제출 자료에는 최신 기획서를 사용합니다.
SQL Server 초기 계획은 설치 없는 배포를 위해 SQLite로 조정했습니다.
전체 시스템을 외부 서비스로 공개하려면 인증·권한과 운영 설계를 추가해야 합니다.

## 외부 구성
모델 가중치와 Ollama는 별도입니다. setup 절차와 라이선스 링크는 제출안내에 있습니다.
Ollama API https://docs.ollama.com/api/chat


## 발표 전 진단
제출 결과물의 runtime/환경진단.cmd 또는 QualityMate.exe --diagnose를 실행합니다.
필수 파일, Windows x64, 저장 권한, 설정, AI 모델과 포트를 확인합니다.
종료 코드 0은 준비 완료, 1은 앱 환경 오류, 2는 AI 준비 필요입니다.
모델은 자동 설치하지 않습니다. 환경 점검 결과와 5분 시연·질의응답은 제출안내에 있습니다.
개인 프로젝트로 확정했으며, 화면은 밝은 핑크 코드 배경 테마입니다.

## 2026-11-21 오답 수정
제품 가격·예산·반품 등 표준서에 없는 질문은 모델 호출 없이 거절합니다.
제품 판정·재검사 횟수·LOT는 DB에서 답하고, 지원 절차는 KnowledgeBase.cs가 문장 후보를 고릅니다.
이 버전은 시연 SOP 4개에 맞춘 규칙 기반 검색입니다. 임베딩/BM25 검색으로 설명하지 않습니다.
모델이 근거를 선택하지 않으면 source-only로 표시합니다. 실제 AI 답변과 구분합니다.
실제 모델 회귀 56/56, 모의 모델 계약 11/11을 확인했습니다. source-only 통과를 AI 정확도로 해석하지 않습니다.
파일별 상세 분석: docs/코드분석-2026-11-21.md
재검사/CSV 시험은 별도 DB에서 실행합니다: python -X utf8 -B tools/test_fixed.py
기존 README의 2026-11-19 평가 시간과 제출 문서는 당시 버전의 기록입니다.
