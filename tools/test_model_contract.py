# =====================================================================
# test_model_contract.py  -  모델 응답 계약 시험 (가짜 Ollama 사용)
# =====================================================================
# 목적  : 실제 모델 없이 '모델이 어떤 응답을 돌려줘도 서버가 안전하게 처리하는가'를 확인한다.
#         파이썬이 가짜 Ollama 서버(포트 11439)를 띄우고, 앱(포트 5201)이 --Ollama:BaseUrl로 그 서버를 보도록 실행한다.
# 시험 사례(가짜 서버 응답 -> 기대 mode):
#   정상 번호[0] -> llm / 빈 배열 -> source-only /
#   message 없음, JSON 아님, 범위 밖 번호, 음수, 문자열 번호, 5개 초과(중복), HTTP 503 -> 모두 retrieval-only
#   추가: 잘못된 /api/tags 응답이면 aiReady=false, 답할 수 없는 질문(가격)은 모델을 호출하지 않아야 한다.
# 입력  : 빌드된 bin/Release/net10.0/QualityMate.exe.
# 출력  : 사례별 PASS/FAIL, 'MOCK RESULT 통과/전체', tmp/fix-2026-11-21/model-contract.json 저장.
#         모두 통과하면 종료 코드 0, 아니면 1.
# 실행  : python -X utf8 -B tools/test_model_contract.py  (보통 test_fixed.py가 호출)
# =====================================================================
import json,urllib.request,urllib.error,threading,subprocess,time,sys
from pathlib import Path
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
root=Path(__file__).resolve().parents[1]; state={'case':'valid','calls':0}; results=[]
# 가짜 Ollama: GET은 설치 모델 목록(/api/tags 흉내), POST는 state['case']에 따라 준비된 응답을 돌려준다.
class Handler(BaseHTTPRequestHandler):
    def log_message(self,*a):pass
    def do_GET(self):
        self.send_response(200);self.end_headers();self.wfile.write(json.dumps({} if state['case']=='bad-tags' else {'models':[{'name':'kanana2:3b'}]}).encode())
    # 호출 횟수(state['calls'])를 세어 '모델이 호출되지 않았는지'도 검증할 수 있게 한다.
    def do_POST(self):
        self.rfile.read(int(self.headers.get('Content-Length','0')));state['calls']+=1
        key=state['case'];status=503 if key=='http-error' else 200
        # 사례 이름별 가짜 응답 본문. 정상 응답부터 깨진 JSON, 범위 밖/음수/문자열/과다 개수까지 포함한다.
        choices={'valid':{'message':{'content':'{"evidenceIds":[0]}'}},'empty':{'message':{'content':'{"evidenceIds":[]}'}},'missing-message':{},'bad-json':{'message':{'content':'not JSON'}},'out-of-range':{'message':{'content':'{"evidenceIds":[999]}'}},'negative':{'message':{'content':'{"evidenceIds":[-1]}'}},'wrong-type':{'message':{'content':'{"evidenceIds":["0"]}'}},'too-many':{'message':{'content':'{"evidenceIds":[0,0,0,0,0]}'}}}
        self.send_response(status);self.end_headers();self.wfile.write(json.dumps(choices.get(key,{})).encode())
# 가짜 서버를 백그라운드 스레드로 실행.
server=ThreadingHTTPServer(('127.0.0.1',11439),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
dir=root/'tmp'/'fix-2026-11-21';dir.mkdir(parents=True,exist_ok=True)
# 앱을 가짜 서버로 연결해 실행(--Ollama:BaseUrl) 한다. 데이터 폴더도 새로 만든다.
log=open(dir/'mock-app.log','w',encoding='utf8');p=subprocess.Popen([str(root/'bin/Release/net10.0/QualityMate.exe'),'--urls','http://127.0.0.1:5201','--DataDirectory',str(dir/('mock-data-'+str(time.time_ns()))),'--Ollama:BaseUrl','http://127.0.0.1:11439'],cwd=root,stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
# 앱 API 호출 도우미(상태 코드와 JSON 반환).
def call(path,body=None):
    r=urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:5201'+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json'}),timeout=10)
    return r.status,json.loads(r.read())
try:
    for _ in range(50):
        try:call('/api/inspections');break
        except Exception:time.sleep(.2)
    # 사례 반복: 가짜 응답을 바꿔 /api/chat을 호출하고 mode와 근거 절(SOP-03)이 기대와 같은지 확인한다.
    for name,mode in [('valid','llm'),('empty','source-only'),('missing-message','retrieval-only'),('bad-json','retrieval-only'),('out-of-range','retrieval-only'),('negative','retrieval-only'),('wrong-type','retrieval-only'),('too-many','retrieval-only'),('http-error','retrieval-only')]:
        state['case']=name;code,r=call('/api/chat',{'productId':'QM-001','question':'재검사에 필요한 기록은 무엇인가요?'})
        ok=code==200 and r['mode']==mode and [s['id'] for s in r['sources']]==['SOP-03'];results.append(dict(name=name,pass_=ok,mode=r['mode']));print(('PASS ' if ok else 'FAIL ')+name+' -> '+r['mode'])
    # 상태 확인 API가 이상한 목록을 받아도 500이 아니라 aiReady=false가 되는지 확인.
    state['case']='bad-tags';code,r=call('/api/status');ok=code==200 and r['aiReady'] is False;results.append(dict(name='malformed tags status',pass_=ok));print(('PASS ' if ok else 'FAIL ')+'malformed tags status')
    before=state['calls'];code,r=call('/api/chat',{'productId':'QM-002','question':'제품 가격은 얼마인가요?'});ok=r['mode']=='no-evidence' and state['calls']==before;results.append(dict(name='unanswerable skips LLM',pass_=ok));print(('PASS ' if ok else 'FAIL ')+'unanswerable skips LLM')
finally:p.terminate();p.wait(timeout=10);server.shutdown();log.close()
(dir/'model-contract.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf8');print('MOCK RESULT',sum(x['pass_'] for x in results),'/',len(results));sys.exit(0 if all(x['pass_'] for x in results) else 1)
