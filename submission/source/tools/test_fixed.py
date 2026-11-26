"""Run destructive state tests only on a fresh, isolated SQLite directory."""
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
