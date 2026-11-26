from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
import hashlib, json, shutil, re, xml.etree.ElementTree as ET

root=Path(__file__).resolve().parents[1]
submission=root/"submission"
source=submission/"source";source.mkdir(exist_ok=True)
for name in ("obj", "bin"):
    target=source/name
    if target.exists() and target.resolve().parent == source.resolve(): shutil.rmtree(target)
for name in ["KnowledgeBase.cs","Program.cs","RuntimeDiagnostics.cs","InspectionStore.cs","VisionCsv.cs","QualityMate.csproj","appsettings.json","README.md",".gitignore","시작하기.cmd"]:
    shutil.copy2(root/name,source/name)
for name in ["wwwroot","knowledge","samples","tools","docs"]:
    shutil.copytree(root/name,source/name,dirs_exist_ok=True,ignore=shutil.ignore_patterns("__pycache__","*.pyc"))
samples=submission/"samples";shutil.copytree(root/"samples",samples,dirs_exist_ok=True)
# Application source builds independently of the document-authoring runtime.
with ZipFile(submission/"documents"/"QualityMate_최종발표.pptx") as deck:
    notes=[]
    ns={"a":"http://schemas.openxmlformats.org/drawingml/2006/main"}
    for i in sorted(int(re.search(r"notesSlide(\d+)", n).group(1)) for n in deck.namelist() if re.fullmatch(r"ppt/notesSlides/notesSlide\d+\.xml", n)):
        text=ET.fromstring(deck.read(f"ppt/notesSlides/notesSlide{i}.xml"))
        paragraphs=["".join(p.itertext()) for p in text.findall(".//a:t",ns)]
        notes.append(f"## 슬라이드 {i}\n\n"+" ".join(paragraphs))
    (submission/"documents"/"발표자노트.md").write_text("# QualityMate 발표자 노트\n\n"+"\n\n".join(notes),encoding="utf-8")
file_rows=[]
for file in sorted(submission.rglob("*")):
    if file.is_file():
        rel=file.relative_to(submission).as_posix()
        if rel == 'manifest.json':
            continue
        file_rows.append({"file":rel,"bytes":file.stat().st_size,"sha256":hashlib.sha256(file.read_bytes()).hexdigest()})
(submission/"manifest.json").write_text(json.dumps({"project":"QualityMate","date":"2026-11-21","files":file_rows},ensure_ascii=False,indent=2),encoding="utf-8")
dest=root/"output";dest.mkdir(exist_ok=True)
archive=dest/"QualityMate_오류수정_코드분석_2026-11-21.zip"
with ZipFile(archive,"w",ZIP_DEFLATED,compresslevel=6) as z:
    for file in sorted(submission.rglob("*")):
        if file.is_file():z.write(file,Path("QualityMate")/file.relative_to(submission))
with ZipFile(archive) as z:
    corrupt=z.testzip()
    assert corrupt is None,corrupt
    expected=["QualityMate/documents/QualityMate_기획서.pdf","QualityMate/documents/QualityMate_결과보고서.pdf","QualityMate/documents/QualityMate_최종발표.pptx","QualityMate/source/Program.cs","QualityMate/runtime/QualityMate.exe","QualityMate/runtime/knowledge/standards.json","QualityMate/runtime/실행하기.cmd"]
    for item in expected:assert item in z.namelist(),item
    assert not any("/runtime/data/" in n or "/runtime/tmp/" in n or "/runtime/tools/" in n for n in z.namelist())
    assert not any(n.endswith(".env") for n in z.namelist())
print(json.dumps({"archive":str(archive),"bytes":archive.stat().st_size,"entries":len(file_rows)+1,"sha256":hashlib.sha256(archive.read_bytes()).hexdigest(),"crcCheck":"passed"},ensure_ascii=False))

