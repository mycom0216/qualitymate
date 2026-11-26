import json,urllib.request,urllib.error,threading,subprocess,time,sys
from pathlib import Path
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
root=Path(__file__).resolve().parents[1]; state={'case':'valid','calls':0}; results=[]
class Handler(BaseHTTPRequestHandler):
    def log_message(self,*a):pass
    def do_GET(self):
        self.send_response(200);self.end_headers();self.wfile.write(json.dumps({} if state['case']=='bad-tags' else {'models':[{'name':'kanana2:3b'}]}).encode())
    def do_POST(self):
        self.rfile.read(int(self.headers.get('Content-Length','0')));state['calls']+=1
        key=state['case'];status=503 if key=='http-error' else 200
        choices={'valid':{'message':{'content':'{"evidenceIds":[0]}'}},'empty':{'message':{'content':'{"evidenceIds":[]}'}},'missing-message':{},'bad-json':{'message':{'content':'not JSON'}},'out-of-range':{'message':{'content':'{"evidenceIds":[999]}'}},'negative':{'message':{'content':'{"evidenceIds":[-1]}'}},'wrong-type':{'message':{'content':'{"evidenceIds":["0"]}'}},'too-many':{'message':{'content':'{"evidenceIds":[0,0,0,0,0]}'}}}
        self.send_response(status);self.end_headers();self.wfile.write(json.dumps(choices.get(key,{})).encode())
server=ThreadingHTTPServer(('127.0.0.1',11439),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
dir=root/'tmp'/'fix-2026-11-21';dir.mkdir(parents=True,exist_ok=True)
log=open(dir/'mock-app.log','w',encoding='utf8');p=subprocess.Popen([str(root/'bin/Release/net10.0/QualityMate.exe'),'--urls','http://127.0.0.1:5201','--DataDirectory',str(dir/('mock-data-'+str(time.time_ns()))),'--Ollama:BaseUrl','http://127.0.0.1:11439'],cwd=root,stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
def call(path,body=None):
    r=urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:5201'+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json'}),timeout=10)
    return r.status,json.loads(r.read())
try:
    for _ in range(50):
        try:call('/api/inspections');break
        except Exception:time.sleep(.2)
    for name,mode in [('valid','llm'),('empty','source-only'),('missing-message','retrieval-only'),('bad-json','retrieval-only'),('out-of-range','retrieval-only'),('negative','retrieval-only'),('wrong-type','retrieval-only'),('too-many','retrieval-only'),('http-error','retrieval-only')]:
        state['case']=name;code,r=call('/api/chat',{'productId':'QM-001','question':'재검사에 필요한 기록은 무엇인가요?'})
        ok=code==200 and r['mode']==mode and [s['id'] for s in r['sources']]==['SOP-03'];results.append(dict(name=name,pass_=ok,mode=r['mode']));print(('PASS ' if ok else 'FAIL ')+name+' -> '+r['mode'])
    state['case']='bad-tags';code,r=call('/api/status');ok=code==200 and r['aiReady'] is False;results.append(dict(name='malformed tags status',pass_=ok));print(('PASS ' if ok else 'FAIL ')+'malformed tags status')
    before=state['calls'];code,r=call('/api/chat',{'productId':'QM-002','question':'제품 가격은 얼마인가요?'});ok=r['mode']=='no-evidence' and state['calls']==before;results.append(dict(name='unanswerable skips LLM',pass_=ok));print(('PASS ' if ok else 'FAIL ')+'unanswerable skips LLM')
finally:p.terminate();p.wait(timeout=10);server.shutdown();log.close()
(dir/'model-contract.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf8');print('MOCK RESULT',sum(x['pass_'] for x in results),'/',len(results));sys.exit(0 if all(x['pass_'] for x in results) else 1)
