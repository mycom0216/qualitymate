# QualityMate 요구사항 명세서

작성일 2026-11-26 · 작성자 반재영 · 버전 1.0 · 우선순위: M(Must) S(Should) C(Could)

## 1. 기능 요구사항

| ID | 요구사항 | 상세 명세 | 우선 | 수용 기준(검증 방법) | 구현 | 상태 |
|---|---|---|---|---|---|---|
| FR-01 | 검사 이력 조회 | `GET /api/inspections`: 제품별 Id·Lot·최초 판정(InitialResult)·관찰(Defect)·검사 시각·재검사 목록·`revision`(재검사 수)·`currentResult`(마지막 재검사 결과, 없으면 최초). 하나의 읽기 트랜잭션에서 최초·재검사를 함께 읽음 | M | 시드 제품 3개 조회, 재시작 후에도 이력 보존 | InspectionStore.All/Read | 완료 |
| FR-02 | 표준서 조회 | `GET /api/documents`: 표준서 4개 절(id·제목·버전·원문) | S | 4개 절 반환 | KnowledgeBase | 완료 |
| FR-03 | 챗봇 질문 처리 | `POST /api/chat` {productId, question}. 질문 1~2,000자(빈 값·초과 400), 제품 없음 404. 응답에 mode·answer·facts(제품 사실)·sources(절 목록)·model | M | 빈 질문 400, 없는 제품 404, 정상 질문에 mode·sources 포함 | Program.cs | 완료 |
| FR-04 | 제품 기록 질문 | "현재/최초 판정", "재검사 몇 번", "LOT"는 모델 없이 DB 값으로 답(`mode=record`) | M | 판정·횟수·LOT 질문이 DB 값과 일치, 모델 호출 없음 | KnowledgeBase.Plan | 완료 |
| FR-05 | 범위 밖 질문 거절 | 표준서에 없는 주제(가격·비용·예산·반품·폐기·날씨·주식·여행 등)는 **모델을 호출하지 않고** `mode=no-evidence`, "관련 표준서 근거를 찾지 못했습니다" | M | "제품 가격은 얼마인가요?", "내일 날씨" → no-evidence, sources 0건 | KnowledgeBase.Plan | 완료 |
| FR-06 | 근거 선택형 답변 | 질문 의도에 맞는 원문 문장 후보를 만들고 0부터 번호를 붙여 LLM에 전달. 응답은 JSON 스키마 `{"evidenceIds":[정수…]}`(최대 4개, temperature 0). 서버가 **원시 배열 길이 ≤ 4, 정수, 후보 범위 안**을 검증하고 해당 번호의 **원문+절 ID**만 출력(`mode=llm`) | M | 고정 질문 20개 통과(관련 절·인용·원문 일치·필수 구절), 잘못된 번호·음수·문자열·길이 초과는 거부 | Program.cs, KnowledgeBase | 완료 |
| FR-07 | 응답 모드 구분 | 선택 번호가 비면 `source-only`(검색 원문 표시, "AI 선택 없음" 안내, model=null), 통신·JSON·번호 오류면 `retrieval-only`("AI 연결을 사용할 수 없어 검색 원문 표시"). 두 경우 모두 AI 답변 성공으로 세지 않음 | M | 모의 모델 계약 11개(빈 선택·누락·JSON 아님·범위 밖·음수·타입 오류 등) | Program.cs | 완료 |
| FR-08 | 재검사 저장 | `POST /api/inspections/{id}/reinspections` {result(PASS·FAIL), operator(≤50자), note(≤1,000자), expectedRevision, requestId(GUID)}. 검사자·메모는 필수·앞뒤 공백 제거. 최초 검사 행은 수정하지 않고 재검사 행을 추가 | M | 최초 FAIL 보존·최신 PASS, 빈 검사자 400, 없는 제품 404 | InspectionStore.Append | 완료 |
| FR-09 | 중복 요청 방지 | `RequestId` UNIQUE. 같은 ID·같은 제품·같은 내용이면 이전 성공 결과를 그대로 반환(재시도 안전), 같은 ID에 다른 제품·다른 내용이면 409 | M | idempotent retry, 같은 ID 다른 내용 409 | InspectionStore.Append | 완료 |
| FR-10 | 버전 충돌 처리 | `expectedRevision`이 현재 재검사 횟수와 다르면 409("다른 검사자가 기록을 변경했습니다"). 비교와 삽입이 같은 쓰기 트랜잭션 안이라 동시 저장 시 하나만 성공 | M | stale revision rejected, 동시 저장 시험 | InspectionStore.Append | 완료 |
| FR-11 | VisionQC CSV 가져오기 | `POST /api/import/visionqc` {csv}. 1MB 이하, 헤더 8개(Id, JobId, InspectedAtUtc, OriginalVerdict, DefectScore, ElapsedMs, ImagePath, ErrorMessage) 일치, 최대 1,000행. OK→PASS, NG→FAIL(구체 불량 유형 미확인·점수 보존), ERROR→ERROR 유지. 제품 키 `VQC-{JobId}-{Id}`, LOT `VQC-JOB-{JobId}`. 파일 전체를 먼저 검증하고 하나의 트랜잭션으로 저장 | M | 3행 가져오기 added=3, 재가져오기 added=0·skipped=3, 같은 ID 다른 내용 409(전체 롤백), 형식·판정·시각·점수·중복 ID·크기 오류 400 | VisionCsv, InspectionStore.Import | 완료 |
| FR-12 | AI 상태 확인 | `GET /api/status`: Ollama `/api/tags`(3초 제한)에서 설정 모델이 설치됐는지 `aiReady`로 반환(연결·JSON 오류면 false). 생성 성공까지 보장하지는 않음 | S | 모델 있음/없음/연결 불가 | Program.cs | 완료 |
| FR-13 | 환경 진단 | `QualityMate.exe --diagnose`: 필수 파일·Windows x64·쓰기 권한·설정(로컬 http 주소·Ollama 주소·모델)·AI 모델·포트(이미 실행 중인 QualityMate 인식)를 점검하고 `environment-check.json` 저장. 종료 코드 0 준비 완료 · 1 앱 환경 오류 · 2 AI 준비 필요. 모델은 자동 설치하지 않음 | S | 정상 0, 모델 없음 2, 잘못된 설정 1 | RuntimeDiagnostics | 완료 |
| FR-14 | 영속 저장 | SQLite 파일 `data/qualitymate.db`(외래 키 ON, WAL). 기존 `inspections.json`이 있으면 새 DB 초기 생성 때 이관하고 원본은 보존. DB가 비면 시연 제품 3개 시드 | M | 재시작 보존, JSON 이관 | InspectionStore 생성자 | 완료 |
| FR-15 | 화면 | 제품 선택, 질문 입력과 근거(절 ID·원문) 확인, 재검사 입력 폼, CSV 가져오기, AI 연결 상태, 응답 모드 배지. 서버에서 온 값은 화면에 넣기 전에 모두 `escapeHtml`로 HTML 이스케이프(&, <, >, 따옴표) | S | 브라우저 확인(데스크톱 1440×1000·모바일 390×844, 가로 넘침 없음) | wwwroot/ | 완료 |

## 2. 비기능 요구사항

| ID | 분류 | 명세 | 우선 | 검증 | 상태 |
|---|---|---|---|---|---|
| NFR-01 | 정확성 | 사용자에게 보이는 답은 표준서 원문 또는 DB 값만. 모델의 자유 문장은 노출하지 않음 | M | 고정 질문 원문 일치 검사 | 완료 |
| NFR-02 | 정합성 | 재검사·CSV는 트랜잭션, 최초 기록 불변, UNIQUE·CHECK·FK로 DB가 규칙 강제 | M | 기능 검사 17개 | 완료 |
| NFR-03 | 개인정보 | 로컬 처리(127.0.0.1:5187), 로컬 LLM, 외부 전송 없음 | M | 설정 확인, 진단 | 완료 |
| NFR-04 | 보안 | SQL 매개변수 바인딩, 입력 길이 제한(질문 2,000·검사자 50·메모 1,000·CSV 1MB·1,000행), 화면 출력 시 HTML 이스케이프, 오류 시 예외 종류만 로그 | M | 입력 검증 시험 | 완료 |
| NFR-05 | 가용성 | AI가 없어도 검색 원문으로 동작(`retrieval-only`) | S | 모의 모델 계약 | 완료 |
| NFR-06 | 성능 | LLM 호출 질문 응답 중앙값 약 3초, 전체 최대 약 4초(로컬 측정) | S | 평가 도구 | 완료(2026-11-19 측정) |
| NFR-07 | 이식성 | Windows x64 자체 포함 실행 파일, 소스 빌드는 .NET 10 SDK | S | 패키지 시험 | 완료 |
| NFR-08 | 유지보수 | 모든 소스에 "무엇을·왜" 주석, 기능·질문·회귀·진단 시험 도구 제공 | S | 시험 도구 실행 | 완료 |

## 3. 인터페이스 명세(API)

| 메서드·경로 | 설명 | 주요 입력 | 응답 |
|---|---|---|---|
| GET /api/inspections | 검사 이력 목록 | – | 제품 배열(재검사 포함, revision, currentResult) |
| GET /api/documents | 표준서 절 목록 | – | id·title·version·text |
| GET /api/status | AI 준비 상태 | – | aiReady, model, storage, demo |
| POST /api/chat | 질문 | productId, question | mode, answer, facts, sources, notice, model / 400·404 |
| POST /api/import/visionqc | CSV 가져오기 | csv | added, skipped / 400·409 |
| POST /api/inspections/{id}/reinspections | 재검사 저장 | result, operator, note, expectedRevision, requestId | 갱신된 제품 / 400·404·409 |

오류 코드: 400 입력 오류 · 404 대상 없음 · 409 충돌(같은 요청 ID 다른 내용, 오래된 버전, 기존 검사와 내용 다름).

응답 모드: `record`(DB 값) · `no-evidence`(근거 없음) · `llm`(AI가 선택한 원문) · `source-only`(AI 선택 없음) · `retrieval-only`(AI 오류).

## 4. 데이터 요구사항

SQLite 테이블 2개(Inspections, Reinspections)와 표준서 JSON 파일. 판정 CHECK, RequestId UNIQUE, 외래 키, 인덱스 → 「데이터정의서」 참조.

## 5. 검증 매핑

| 요구사항 | 시험 | 결과 |
|---|---|---|
| FR-01·14 | 기능 검사: seed products, SQLite database created, restart preserves SQLite history | 통과 |
| FR-03 | 기능 검사: empty question rejected, unknown product rejected | 통과 |
| FR-04·05 | 고정 질문 중 범위 밖 4개, 회귀 56개, `unsupported question` | 통과 |
| FR-06 | 고정 질문 20개(관련 절·인용·원문 일치·필수 구절), live LLM and relevant evidence | 통과 |
| FR-07 | 모의 모델 계약 11개 | 통과 |
| FR-08·09·10 | preserve original and append, idempotent retry, stale revision rejected, invalid operator rejected | 통과 |
| FR-11 | VisionQC NG and ERROR import, duplicate CSV idempotent, conflicting source rejected, invalid CSV rejected, atomic import | 통과 |
| FR-13 | 진단 3건: 정상 0 · 모델 없음 2 · 잘못된 설정 1 | 통과 |
| FR-12·15 | 브라우저 확인 | 통과 |
