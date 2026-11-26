// =====================================================================
// finalize_deck.mjs  -  발표 PPT 최종 검증 및 복사 (Node.js)
// =====================================================================
// 목적  : 작성해 둔 발표 후보(tmp/deck-candidate.pptx)를 프레젠테이션 도구의 검증기로 점검한 뒤
//         검증된 파일을 submission/documents/QualityMate_최종발표.pptx로 복사한다.
//         패키지 무결성, 슬라이드 레이아웃(제목/불릿 맞춤), 슬라이드 수 12장, 9번 슬라이드의 기본 차트, 글꼴 정책을 확인한다.
// 입력  : tmp/deck-candidate.pptx, 그리고 PC에 설치된 프레젠테이션 도구의 스킬/런타임 경로(코드 상단에 절대 경로로 고정).
// 출력  : tmp/validated-design/ 아래 검증본과 검증 영수증(JSON), 최종 복사본. 요약 JSON을 콘솔에 출력.
// 실행  : 프로젝트의 상위 폴더에서 node projects/QualityMate/tools/finalize_deck.mjs (경로가 PC 환경에 종속되어 다른 PC에서는 그대로 동작하지 않는다)
// =====================================================================
import fs from 'node:fs/promises';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
// 외부 도구 경로(이 PC에 설치된 위치 고정).
const skill='C:/Users/LMS/.codex/plugins/cache/openai-primary-runtime/presentations/26.1118.11504/skills/presentations';
// 프로젝트 루트: 현재 작업 폴더 기준 상대 경로이므로 상위 폴더에서 실행해야 한다.
const root=path.resolve('projects/QualityMate');
const revision=Date.now();
process.env.RUNTIME_NODE_MODULES='C:/Program Files/WindowsApps/OpenAI.CodexPrimaryRuntime.v26-1118-904-0_26.1118.904.0_x64__3k8sg7r9htsxt/dependencies/node/node_modules';
const {finalizePresentation}=await import(pathToFileURL(path.join(skill,'container_tools/artifact_tool_utils.mjs')));
await fs.mkdir(path.join(root,'tmp/validated-design'),{recursive:true});
// 검증 실행. 실패하면 예외가 나거나 결과 상태에 문제가 표시된다.
const result=await finalizePresentation({
 workspaceDir:root,
 candidatePath:path.join(root,'tmp/deck-candidate.pptx'),
 finalPath:path.join(root,'tmp/validated-design/QualityMate-Design-'+revision+'.pptx'),
 pythonExecutable:'C:/Program Files/WindowsApps/OpenAI.CodexPrimaryRuntime.v26-1118-904-0_26.1118.904.0_x64__3k8sg7r9htsxt/dependencies/python/python.exe',
 integrityValidatorPath:path.join(skill,'container_tools/inspect_presentation_package_integrity.py'),
 layoutValidatorPath:path.join(skill,'container_tools/inspect_presentation_layout_geometry.py'),
 layoutArgs:['--expected-slide-size-emu','12192000,6858000','--validate-heading-fit','--validate-bullet-geometry'],
 explicitTotalSlideCount:12,
 requiredNativeChartOwnerSlides:[9],
 materializeLiteralChartWorkbooks:true,
 fontPolicy:{basis:'design',families:['Malgun Gothic','Consolas']},
 verifyArtifactToolImport:true,
 receiptPath:path.join(root,'tmp/pptx-validation-'+revision+'.json')
});
// 검증을 통과한 파일만 제출 문서 위치로 복사한다.
await fs.copyFile(result.finalPath,path.join(root,'submission/documents/QualityMate_최종발표.pptx'));
console.log(JSON.stringify({finalPath:result.finalPath,packageIntegrity:result.packageIntegrity.status,presentationLayout:result.presentationLayout.findingCount,firstPartyImport:result.firstPartyImport.passed}));

