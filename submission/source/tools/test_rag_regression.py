import json,sys,urllib.request,urllib.error,time,uuid,concurrent.futures
from pathlib import Path
base=sys.argv[1] if len(sys.argv)>1 else 'http://127.0.0.1:5200'
root=Path(__file__).resolve().parents[1]
results=[]
modes={}
def api(path,body=None):
    req=urllib.request.Request(base+path,data=None if body is None else json.dumps(body,ensure_ascii=False).encode(),headers={'Content-Type':'application/json'})
    try:
        with urllib.request.urlopen(req,timeout=120) as r:return r.status,json.loads(r.read())
    except urllib.error.HTTPError as e:return e.code,json.loads(e.read())
def check(name,ok,detail=None):
    results.append(dict(name=name,pass_=bool(ok),detail=detail));print(('PASS ' if ok else 'FAIL ')+name,flush=True)
def chat(q,p='QM-001'):
    r=api('/api/chat',dict(productId=p,question=q))[1]
    modes[r['mode']]=modes.get(r['mode'],0)+1
    return r
for i in range(40):
    try:api('/api/status');break
    except Exception:time.sleep(.25)
for q in json.loads((root/'tools/questions.json').read_text(encoding='utf-8-sig')):
    r=chat(q['question'],q['productId']);expected=q.get('expectedSource');ok=(r['mode'] in ('llm','source-only') and expected in [s['id'] for s in r['sources']] and q['expectedText'] in r['answer']) if expected else r['mode']=='no-evidence'
    check('fixed-'+str(q['id']),ok,None if ok else r)
for q in ['제품 가격은 얼마인가요?','검사 장비 구입 예산은 얼마인가요?','재검사 비용은 얼마인가요?','흠집 제품은 반품하나요?','이물 제품은 폐기해도 되나요?','통화 기록 삭제 방법','음식 보관 방법','분리수거 방법','제품 사진은 어떻게 찍나요?','검사 장비 구매','서울 맛집 추천','원인 분석 비용은?']:
    r=chat(q);check('reject: '+q,r['mode']=='no-evidence',None if r['mode']=='no-evidence' else r)
for q,p,phrase in [('다시 검사할 때 뭘 적어야 해요?','QM-001','검사자'),('불량 원인이 뭔가요?','QM-003','원인을 확정하지'),('흠집 원인을 확정할 수 있나요? 그리고 재검사 기록에는 무엇이 필요한가요?','QM-001','검사자'),('이물 제품은 어떻게 보관하고 재검사할 때 뭘 기록해야 하나요?','QM-003','분리 보관')]:
    r=chat(q,p);ok=r['mode'] in ('llm','source-only') and phrase in r['answer']
    if '그리고' in q:ok=ok and '원인을 확정하지' in r['answer']
    if '보관하고' in q:ok=ok and '검사자' in r['answer']
    check('supported: '+q,ok,None if ok else r)
for q,p,phrase in [('왜 불합격인가요?','QM-002','불합격 기록이 없'),('이 제품의 최초 판정과 현재 판정은?','QM-001','불합격'),('재검사 횟수는 몇 번인가요?','QM-001','0회'),('이 제품 LOT는?','QM-001','LOT-DEMO-A')]:
    r=chat(q,p);check('record: '+q,r['mode']=='record' and phrase in r['answer'],r)
for q in ['검사 장비 구입 예산은 얼마인가요?','흠집 제품은 반품하나요?']:
    # Rejections never invoke the LLM and must never carry standard sections.
    r=chat(q);check('no false citations: '+q,not r['sources'] and r['model'] is None)
check('empty chat 400',api('/api/chat',dict(productId='QM-001',question=''))[0]==400)
check('missing product 404',api('/api/chat',dict(productId='MISSING',question='재검사'))[0]==404)
req=dict(result='PASS',operator='회귀 테스트',note='표준서와 관찰 확인',expectedRevision=0,requestId=str(uuid.uuid4()))
a,b=api('/api/inspections/QM-001/reinspections',req),api('/api/inspections/QM-001/reinspections',req)
check('append preserves original; idempotent retry',a[0]==b[0]==200 and a[1]['initialResult']=='FAIL' and b[1]['currentResult']=='PASS' and b[1]['revision']==1)
check('same request different content 409',api('/api/inspections/QM-001/reinspections',{**req,'note':'changed'})[0]==409)
check('stale revision 409',api('/api/inspections/QM-001/reinspections',{**req,'requestId':str(uuid.uuid4())})[0]==409)
with concurrent.futures.ThreadPoolExecutor(2) as ex:
    statuses=list(ex.map(lambda _:api('/api/inspections/QM-003/reinspections',{**req,'requestId':str(uuid.uuid4())})[0],range(2)))
check('concurrent writers 200/409',sorted(statuses)==[200,409],statuses)
csv=(root/'samples/VisionQC-demo.csv').read_text(encoding='utf-8-sig')
a=api('/api/import/visionqc',dict(csv='\ufeff'+csv));b=api('/api/import/visionqc',dict(csv=csv))
check('UTF-8 BOM CSV accepted; duplicate skipped',a[0]==b[0]==200 and a[1]['added']==3 and b[1]['skipped']==3)
rows=api('/api/inspections')[1];check('ERROR preserved',next(x for x in rows if x['id']=='VQC-2-3')['initialResult']=='ERROR')
r=chat('왜 불합격인가요?','VQC-2-3');check('ERROR is not a failed inspection',r['mode']=='record' and '검사 오류' in r['answer'])
r=chat('불량 원인이 뭔가요?','VQC-2-1');check('unknown NG defect not invented',r['mode']=='record' and '불량 유형' in r['answer'])
check('timestamp without timezone rejected',api('/api/import/visionqc',dict(csv=csv.replace('Z','')))[0]==400)
check('UTF-8 bytes over 1 MB rejected',api('/api/import/visionqc',dict(csv='가'*333334))[0]==400)
check('invalid header rejected',api('/api/import/visionqc',dict(csv='bad,header\n1,2'))[0]==400)
check('conflicting CSV rejected',api('/api/import/visionqc',dict(csv=csv.replace('0.75','0.81')))[0]==409)
passed=sum(x['pass_'] for x in results);print(f'RESULT {passed}/{len(results)} MODES {modes}',flush=True)
dest=root/'tmp'/'fix-2026-11-21';dest.mkdir(parents=True,exist_ok=True);(dest/'regression.json').write_text(json.dumps(dict(base=base,results=results,modes=modes,passed=passed,total=len(results)),ensure_ascii=False,indent=2),encoding='utf8')
sys.exit(0 if passed==len(results) else 1)
