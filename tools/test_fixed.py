"""Run destructive state tests only on a fresh, isolated SQLite directory."""
# =====================================================================
# test_fixed.py  -  재검사/CSV 상태 변경 시험 (격리된 새 DB에서만 실행)
# =====================================================================
# 목적  : 데이터를 바꾸는 시험(재검사 저장, CSV 가져오기)은 항상 새 임시 DB 폴더에서 돌려
#         시연 데이터를 보호한다. 순서:
#           1) 서버 시작(포트 5202) -> test_rag_regression.py 실행
#           2) 서버 재시작 -> 재시작 후에도 QM-001의 재검사 1건과 총 6건이 유지되는지 확인
#           3) test_model_contract.py(가짜 Ollama 계약 시험) 실행
# 입력  : 빌드된 bin/Release/net10.0/QualityMate.exe (없으면 dotnet build -c Release를 안내하며 종료).
# 출력  : 항목별 PASS/FAIL, 마지막에 'ALL CHECKS PASSED'와 데이터 보관 위치.
# 실행  : python -X utf8 -B tools/test_fixed.py
# =====================================================================
import json,subprocess,time,urllib.request,sys
from pathlib import Path
root=Path(__file__).resolve().parents[1]
fixture=root/'tmp'/('fix-test-'+str(time.time_ns()));fixture.mkdir(parents=True)
base='http://127.0.0.1:5202'
exe=root/'bin/Release/net10.0/QualityMate.exe'
if not exe.exists():raise SystemExit('Run dotnet build -c Release first.')
def start(label):
    log=open(fixture/(label+'.log'),'w',encoding='utf8')
    p=subprocess.Popen([str(exe),'--urls',base,'--DataDirectory',str(fixture)],cwd=root,stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
    return p,log
server,log=start('server')
try:
    subprocess.run([sys.executable,'-X','utf8','-B',str(root/'tools/test_rag_regression.py'),base],check=True)
    server.terminate();server.wait(timeout=10);log.close()
    server,log=start('restart')
    for _ in range(40):
        try:
            items=json.loads(urllib.request.urlopen(base+'/api/inspections',timeout=2).read());break
        except Exception:time.sleep(.25)
    else:raise RuntimeError('Server did not restart')
    item=next(x for x in items if x['id']=='QM-001')
    assert len(items)==6 and item['revision']==1 and item['initialResult']=='FAIL' and item['currentResult']=='PASS', 'Restart persistence failed'
    print('PASS restart persistence',flush=True)
    subprocess.run([sys.executable,'-X','utf8','-B',str(root/'tools/test_model_contract.py')],check=True)
finally:
    if server.poll() is None:server.terminate();server.wait(timeout=10)
    log.close()
print('ALL CHECKS PASSED; data retained at '+str(fixture))
