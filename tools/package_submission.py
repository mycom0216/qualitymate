# =====================================================================
# package_submission.py  -  제출 묶음 만들기
# =====================================================================
# 목적  : 소스(코드, wwwroot, knowledge, samples, tools, docs)를 submission/source로 복사하고,
#         발표 PPT의 발표자 노트를 Markdown으로 뽑고, 모든 파일의 크기와 SHA-256을 manifest.json에 기록한 뒤
#         output/ 아래 ZIP으로 압축해 검증(깨진 파일 확인, 필수 파일 포함, 데이터/비밀 파일 제외)한다.
# 입력  : submission/documents/QualityMate_최종발표.pptx 등 이미 만들어진 제출 자료.
# 출력  : output/QualityMate_오류수정_코드분석_2026-11-21.zip 과 그 요약 JSON(크기, 해시, 항목 수)을 콘솔에 출력.
# 실행  : python -X utf8 tools/package_submission.py
# 주의  : 소스 파일 목록은 코드 안에 직접 적혀 있으므로 새 소스 파일을 추가하면 목록도 고쳐야 한다.
# =====================================================================
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
import hashlib, json, shutil, re, xml.etree.ElementTree as ET

root=Path(__file__).resolve().parents[1]
submission=root/"submission"
source=submission/"source";source.mkdir(exist_ok=True)
# 이전 복사본의 빌드 결과(obj, bin)를 지워 소스만 남긴다.
for name in ("obj", "bin"):
    target=source/name
    if target.exists() and target.resolve().parent == source.resolve(): shutil.rmtree(target)
# 루트의 소스/설정 파일 복사(목록에 적힌 파일만).
for name in ["KnowledgeBase.cs","Program.cs","RuntimeDiagnostics.cs","InspectionStore.cs","VisionCsv.cs","QualityMate.csproj","appsettings.json","README.md",".gitignore","시작하기.cmd"]:
    shutil.copy2(root/name,source/name)
# 폴더 단위 복사(__pycache__, .pyc 제외).
for name in ["wwwroot","knowledge","samples","tools","docs"]:
    shutil.copytree(root/name,source/name,dirs_exist_ok=True,ignore=shutil.ignore_patterns("__pycache__","*.pyc"))
samples=submission/"samples";shutil.copytree(root/"samples",samples,dirs_exist_ok=True)
# Application source builds independently of the document-authoring runtime.
# PPT 슬라이드별 발표자 노트 텍스트를 추출해 발표자노트.md로 저장.
with ZipFile(submission/"documents"/"QualityMate_최종발표.pptx") as deck:
    notes=[]
    ns={"a":"http://schemas.openxmlformats.org/drawingml/2006/main"}
    for i in sorted(int(re.search(r"notesSlide(\d+)", n).group(1)) for n in deck.namelist() if re.fullmatch(r"ppt/notesSlides/notesSlide\d+\.xml", n)):
        text=ET.fromstring(deck.read(f"ppt/notesSlides/notesSlide{i}.xml"))
        paragraphs=["".join(p.itertext()) for p in text.findall(".//a:t",ns)]
        notes.append(f"## 슬라이드 {i}\n\n"+" ".join(paragraphs))
    (submission/"documents"/"발표자노트.md").write_text("# QualityMate 발표자 노트\n\n"+"\n\n".join(notes),encoding="utf-8")
# 무결성 목록: 제출 폴더의 모든 파일(manifest.json 자신 제외)의 상대 경로, 바이트 수, SHA-256.
file_rows=[]
for file in sorted(submission.rglob("*")):
    if file.is_file():
        rel=file.relative_to(submission).as_posix()
        if rel == 'manifest.json':
            continue
        file_rows.append({"file":rel,"bytes":file.stat().st_size,"sha256":hashlib.sha256(file.read_bytes()).hexdigest()})
(submission/"manifest.json").write_text(json.dumps({"project":"QualityMate","date":"2026-11-21","files":file_rows},ensure_ascii=False,indent=2),encoding="utf-8")
dest=root/"output";dest.mkdir(exist_ok=True)
# ZIP 압축 후 검증 단계로 이어진다.
archive=dest/"QualityMate_오류수정_코드분석_2026-11-21.zip"
with ZipFile(archive,"w",ZIP_DEFLATED,compresslevel=6) as z:
    for file in sorted(submission.rglob("*")):
        if file.is_file():z.write(file,Path("QualityMate")/file.relative_to(submission))
with ZipFile(archive) as z:
    # ZIP 무결성 검사 및 필수 파일 포함/금지 파일(런타임 data, tmp, tools, .env) 부재 확인. 실패 시 assert로 중단.
    corrupt=z.testzip()
    assert corrupt is None,corrupt
    expected=["QualityMate/documents/QualityMate_기획서.pdf","QualityMate/documents/QualityMate_결과보고서.pdf","QualityMate/documents/QualityMate_최종발표.pptx","QualityMate/source/Program.cs","QualityMate/runtime/QualityMate.exe","QualityMate/runtime/knowledge/standards.json","QualityMate/runtime/실행하기.cmd"]
    for item in expected:assert item in z.namelist(),item
    assert not any("/runtime/data/" in n or "/runtime/tmp/" in n or "/runtime/tools/" in n for n in z.namelist())
    assert not any(n.endswith(".env") for n in z.namelist())
print(json.dumps({"archive":str(archive),"bytes":archive.stat().st_size,"entries":len(file_rows)+1,"sha256":hashlib.sha256(archive.read_bytes()).hexdigest(),"crcCheck":"passed"},ensure_ascii=False))

