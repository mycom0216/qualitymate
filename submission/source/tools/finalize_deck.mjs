import fs from 'node:fs/promises';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
const skill='C:/Users/LMS/.codex/plugins/cache/openai-primary-runtime/presentations/26.1118.11504/skills/presentations';
const root=path.resolve('projects/QualityMate');
const revision=Date.now();
process.env.RUNTIME_NODE_MODULES='C:/Program Files/WindowsApps/OpenAI.CodexPrimaryRuntime.v26-1118-904-0_26.1118.904.0_x64__3k8sg7r9htsxt/dependencies/node/node_modules';
const {finalizePresentation}=await import(pathToFileURL(path.join(skill,'container_tools/artifact_tool_utils.mjs')));
await fs.mkdir(path.join(root,'tmp/validated-design'),{recursive:true});
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
await fs.copyFile(result.finalPath,path.join(root,'submission/documents/QualityMate_최종발표.pptx'));
console.log(JSON.stringify({finalPath:result.finalPath,packageIntegrity:result.packageIntegrity.status,presentationLayout:result.presentationLayout.findingCount,firstPartyImport:result.firstPartyImport.passed}));

