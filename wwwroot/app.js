// =====================================================================
// app.js  -  QualityMate 화면 동작 (프레임워크 없는 순수 JavaScript)
// =====================================================================
// 역할: index.html의 화면을 서버 API와 연결한다.
//   - 제품 목록/상세/이력 표시       (GET  /api/inspections)
//   - 재검사 저장                    (POST /api/inspections/{id}/reinspections)
//   - 품질관리 도우미 질문/답변 표시 (POST /api/chat)
//   - 표준서 목록, AI 상태 배지      (GET  /api/documents, /api/status)
//   - VisionQC CSV 가져오기          (POST /api/import/visionqc)
// 
// 상태 변수(파일 맨 위): products 전체 제품 / selected 선택 제품 /
//   busy 요청 처리 중(중복 클릭 방지) / retryRequest 재검사 재시도용 요청 ID 보관.
// 
// 보안 메모: 서버에서 받은 문자열을 innerHTML에 넣을 때는 반드시 escapeHtml을 거친다(XSS 방지).
// =====================================================================
// 전역 상태. busy가 true인 동안에는 저장/질문/가져오기를 중복 실행하지 않는다.
let products = [], selected = null, busy = false, retryRequest = null;
// 짧은 도우미: id로 요소를 찾는다.
const $ = id => document.getElementById(id);
// HTML 특수문자(& < > " ')를 안전한 문자로 바꾼다. 서버/사용자 입력을 innerHTML로 출력하기 전에 항상 사용한다.
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
// ISO 시각 문자열을 한국어 날짜/시각 표기로 바꾼다.
const when = value => new Date(value).toLocaleString('ko-KR');
// 판정 코드를 화면 문구로: PASS 합격 / ERROR 검사 오류 / 그 외 불합격.
const verdict = result => result === 'PASS' ? '합격' : result === 'ERROR' ? '검사 오류' : '불합격';
// 판정에 맞는 CSS 클래스(fail, error). 합격은 기본 색이라 빈 문자열.
const statusClass = result => result === 'FAIL' ? 'fail' : result === 'ERROR' ? 'error' : '';
// 서버 API 호출 공통 함수. 응답이 성공(2xx)이 아니면 서버가 준 error 메시지로 예외를 던져
// 호출하는 쪽에서 showError로 화면에 알릴 수 있게 한다(400/404/409 모두 같은 방식).
async function api(path, options) {
  const response = await fetch(path, options);
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || '요청을 처리하지 못했습니다.');
  return data;
}
// JSON POST 요청 옵션을 만든다.
function post(body) { return {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}; }
// 왼쪽 제품 목록과 상단 요약 숫자(전체 제품, 최초 불합격, 재검사 기록)를 다시 그린다. 검색 입력(filter)으로 제품 번호를 걸러 보여 준다.
function renderList() {
  const visible = products.filter(p => p.id.toLowerCase().includes($('filter').value.toLowerCase()));
  $('products').innerHTML = visible.map(p => '<button class="product '+(p.id===selected?.id?'active':'')+'" aria-pressed="'+(p.id===selected?.id)+'" data-id="'+escapeHtml(p.id)+'"><span><b>'+escapeHtml(p.id)+'</b><small>'+escapeHtml(p.lot)+'</small></span><span class="pill '+statusClass(p.currentResult)+'">'+verdict(p.currentResult)+'</span></button>').join('') || '<p class="empty">검색 결과가 없습니다.</p>';
  $('selected-product').textContent = selected?.id || '제품 선택';
  $('total').textContent = products.length;
  $('failed').textContent = products.filter(p=>p.initialResult==='FAIL').length;
  $('retests').textContent = products.reduce((n,p)=>n+p.reinspections.length,0);
}
// 가운데 상세 영역을 다시 그린다: 검사 관찰, 최초/현재 판정, 이력 타임라인, 재검사 입력 폼.
// 최초 판정은 항상 그대로 표시되고 재검사는 그 아래에 추가로 나열된다(원본 보존을 화면에서도 드러냄).
function renderDetail() {
  if (!selected) { $('detail').textContent='제품을 선택하세요.'; return; }
  const p = selected;
  $('detail').innerHTML = '<h2>'+escapeHtml(p.id)+' 검사 기록</h2><dl><dt>검사 관찰</dt><dd>'+escapeHtml(p.defect)+'</dd><dt>최초 판정</dt><dd>'+verdict(p.initialResult)+'</dd><dt>현재 판정</dt><dd>'+verdict(p.currentResult)+'</dd><dt>최초 검사 시각</dt><dd>'+when(p.inspectedAt)+'</dd></dl><h3>이력</h3><div class="timeline">최초 검사 · '+verdict(p.initialResult)+'<p>'+escapeHtml(p.defect)+'</p></div>'+p.reinspections.map(r=>'<div class="timeline"><b>재검사 · '+verdict(r.result)+'</b><p>'+escapeHtml(r.note)+'</p><small>'+escapeHtml(r.operator)+' · '+when(r.createdAt)+'</small></div>').join('')+'<h3>재검사 기록 추가</h3><form id="reinspection"><label>판정<select id="result" required><option value="">선택하세요</option><option value="PASS">합격</option><option value="FAIL">불합격</option></select></label><label>검사자<input id="operator" maxlength="50" required autocomplete="name"></label><label>조치 및 확인 메모<textarea id="note" maxlength="1000" required rows="2"></textarea></label><button class="primary" id="save">기록 저장</button><p class="muted">저장 후 최초 검사와 이전 기록을 함께 보존합니다.</p></form>';
}
// 서버에서 전체 제품을 다시 읽고, 이전에 선택한 제품이 있으면 유지한 채 화면을 갱신한다. 저장/가져오기 후에도 호출해 서버의 최신 상태(재검사 횟수 revision 포함)를 반영한다.
async function load() {
  products = await api('/api/inspections');
  selected = products.find(p=>p.id===selected?.id) || products[0];
  renderList(); renderDetail();
}
// 이벤트 연결 - 검색어 입력 시 목록 즉시 필터, 새로고침 버튼은 서버에서 다시 읽기.
$('filter').addEventListener('input', renderList);
$('refresh').addEventListener('click',()=>load().catch(showError));
// 오류 메시지를 화면 알림 영역(#notice)에 표시한다.
function showError(error) { $('notice').textContent=error.message; }
// 제품 선택: 목록 버튼 클릭을 한 곳에서 처리한다(이벤트 위임). 제품을 바꾸면 대화창과 재시도 정보를 비운다. 처리 중(busy)에는 바꾸지 않는다.
$('products').addEventListener('click',e=>{
  const button=e.target.closest('[data-id]'); if(!button || busy) return;
  selected=products.find(p=>p.id===button.dataset.id);
  retryRequest=null; $('conversation').replaceChildren(); renderList(); renderDetail();
});
// 재검사 저장 제출 처리.
//  - expectedRevision: 사용자가 지금 보고 있는 재검사 횟수. 서버가 현재 횟수와 다르면 409(다른 검사자가 먼저 저장)로 거부한다.
//  - requestId: 같은 입력을 다시 보내는 재시도(네트워크 오류 후 재클릭 등)에는 같은 ID를 재사용해 서버가 중복 저장하지 않게 하고(멱등성),
//    입력 내용이 바뀌면 새 ID를 만든다. 성공하면 ID를 비운다.
$('detail').addEventListener('submit',async e=>{
  e.preventDefault(); if(busy) return;
  const body={result:$('result').value,operator:$('operator').value,note:$('note').value,expectedRevision:selected.revision};
  // signature: 입력 내용의 지문. 내용이 같으면 같은 requestId를 재사용하고, 다르면 새로 만든다.
  const signature=JSON.stringify(body);
  if(!retryRequest || retryRequest.signature!==signature) retryRequest={signature,id:crypto.randomUUID()};
  busy=true; $('save').disabled=true; $('notice').textContent='';
  try { await api('/api/inspections/'+encodeURIComponent(selected.id)+'/reinspections',post({...body,requestId:retryRequest.id})); retryRequest=null; await load(); }
  catch(error){ showError(error); }
  finally {busy=false; if($('save')) $('save').disabled=false;}
});
// 추천 질문 버튼: 눌러도 바로 전송하지 않고 입력칸에 채워 사용자가 확인/수정할 수 있게 한다.
document.querySelectorAll('[data-question]').forEach(button=>button.addEventListener('click',()=>{
  $('question').value=button.dataset.question; $('question').focus();
}));
// 질문 전송 처리: 질문 말풍선과 '확인 중' 표시를 먼저 그리고, /api/chat 응답의 mode에 따라 답변 머리표를 구분해 보여 준다.
//  mode: llm(AI 근거 선택) / record(검사 기록 기준) / source-only(표준서 원문, AI 선택 없음) / no-evidence(근거 없음) / 그 외(retrieval-only, AI 연결 필요)
// 답변 본문은 서버가 검증한 표준서 원문이며, [SOP-n] 표기는 인용 배지로 바꿔 보여 준다.
$('chat').addEventListener('submit',async e=>{
  e.preventDefault(); if(busy || !selected) return;
  const question=$('question').value.trim(); if(!question) return;
  busy=true; $('ask').disabled=true; $('notice').textContent='';
  $('conversation').querySelector('.welcome')?.remove();
  const q=document.createElement('div');q.className='bubble question';q.textContent=selected.id+' · '+question;$('conversation').append(q);
  const thinking = document.createElement('div');
  thinking.className = 'thinking';
  thinking.textContent = '관련 근거를 확인하고 있어요…';
  $('conversation').append(thinking);
  try {
    const data=await api('/api/chat',post({productId:selected.id,question}));
    const answer=document.createElement('div'); answer.className='bubble';
    // 서버 mode 값을 사용자가 이해할 수 있는 라벨로 바꾼다. AI가 실제로 근거를 선택한 경우(llm)와 단순 검색 결과를 화면에서 구별한다.
    const mode=data.mode==='llm'?'AI 근거 선택 · '+data.model:data.mode==='record'?'검사 기록 기준':data.mode==='source-only'?'표준서 원문 · AI 선택 없음':data.mode==='no-evidence'?'근거 없음':'검색 결과 · AI 연결 필요';
    const notice = data.notice ? '<p class="facts">'+escapeHtml(data.notice)+'</p>' : '';
    // 먼저 전체를 escapeHtml 한 뒤에만 [SOP-n]을 인용 배지(span)로 바꾼다. 순서가 반대면 서버 문자열이 HTML로 해석될 수 있다.
    const answerText = escapeHtml(data.answer).replace(/\[(SOP-\d+)\]/g, '<span class="cite">$1</span>');
    // 답변 조립: 모드 라벨, 안내문(notice), 답변 원문, DB 사실(facts), 근거 절(접이식 details: 절 ID, 제목, 본문, 버전).
    answer.innerHTML='<span class="mode">'+escapeHtml(mode)+'</span>'+notice+'<div class="answer-copy">'+answerText+'</div><div class="facts">'+escapeHtml(data.facts)+'</div>'+data.sources.map(s=>'<details><summary>['+escapeHtml(s.id)+'] '+escapeHtml(s.title)+'</summary><p>'+escapeHtml(s.text)+'</p><small>'+escapeHtml(s.version)+'</small></details>').join('');
    $('conversation').append(answer); $('question').value='';
    $('conversation').scrollTop=$('conversation').scrollHeight;
  } catch(error){showError(error);}
  finally {thinking.remove();busy=false;$('ask').disabled=false;}
});
// 첫 화면 초기화: 제품 로드 -> 표준서 목록 표시 -> AI 상태 배지 갱신. 어느 단계든 실패하면 showError로 알린다.
async function init() {
  await load();
  const documents=await api('/api/documents');
  $('documents').innerHTML=documents.map(s=>'<details><summary>['+escapeHtml(s.id)+'] '+escapeHtml(s.title)+'</summary><p>'+escapeHtml(s.text)+'</p></details>').join('');
  const status=await api('/api/status');
  $('ai-status').textContent=status.aiReady?'AI 연결됨 · '+status.model:'검색 모드 · AI 연결 필요';
  // AI가 준비되지 않았으면 배지를 오프라인 스타일로 바꾼다(이 경우에도 검색 원문 모드로 사용 가능).
  $('ai-status').classList.toggle('is-offline', !status.aiReady);
}
init().catch(showError);
// VisionQC CSV 가져오기: 선택한 파일을 텍스트로 읽어 /api/import/visionqc로 보낸다.
// 서버는 전부 저장 또는 전부 취소로 처리하므로 결과는 '추가 n건 / 중복 제외 m건' 한 줄로 충분하다. 1MB 초과는 보내기 전에 미리 막는다(서버도 같은 제한).
document.getElementById('import-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (busy) return;
  const file = document.getElementById('csv-file').files[0];
  if (!file || file.size > 1_000_000) { showError(new Error('CSV는 1MB 이하로 선택하세요.')); return; }
  busy = true;
  document.getElementById('import-button').disabled = true;
  try {
    const result = await api('/api/import/visionqc', post({csv: await file.text()}));
    document.getElementById('import-status').textContent = result.added + '건 추가 · ' + result.skipped + '건 중복 제외';
    await load();
  } catch (error) { showError(error); }
  finally { busy = false; document.getElementById('import-button').disabled = false; }
});


