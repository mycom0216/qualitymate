let products = [], selected = null, busy = false, retryRequest = null;
const $ = id => document.getElementById(id);
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const when = value => new Date(value).toLocaleString('ko-KR');
const verdict = result => result === 'PASS' ? '합격' : result === 'ERROR' ? '검사 오류' : '불합격';
const statusClass = result => result === 'FAIL' ? 'fail' : result === 'ERROR' ? 'error' : '';
async function api(path, options) {
  const response = await fetch(path, options);
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || '요청을 처리하지 못했습니다.');
  return data;
}
function post(body) { return {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}; }
function renderList() {
  const visible = products.filter(p => p.id.toLowerCase().includes($('filter').value.toLowerCase()));
  $('products').innerHTML = visible.map(p => '<button class="product '+(p.id===selected?.id?'active':'')+'" aria-pressed="'+(p.id===selected?.id)+'" data-id="'+escapeHtml(p.id)+'"><span><b>'+escapeHtml(p.id)+'</b><small>'+escapeHtml(p.lot)+'</small></span><span class="pill '+statusClass(p.currentResult)+'">'+verdict(p.currentResult)+'</span></button>').join('') || '<p class="empty">검색 결과가 없습니다.</p>';
  $('selected-product').textContent = selected?.id || '제품 선택';
  $('total').textContent = products.length;
  $('failed').textContent = products.filter(p=>p.initialResult==='FAIL').length;
  $('retests').textContent = products.reduce((n,p)=>n+p.reinspections.length,0);
}
function renderDetail() {
  if (!selected) { $('detail').textContent='제품을 선택하세요.'; return; }
  const p = selected;
  $('detail').innerHTML = '<h2>'+escapeHtml(p.id)+' 검사 기록</h2><dl><dt>검사 관찰</dt><dd>'+escapeHtml(p.defect)+'</dd><dt>최초 판정</dt><dd>'+verdict(p.initialResult)+'</dd><dt>현재 판정</dt><dd>'+verdict(p.currentResult)+'</dd><dt>최초 검사 시각</dt><dd>'+when(p.inspectedAt)+'</dd></dl><h3>이력</h3><div class="timeline">최초 검사 · '+verdict(p.initialResult)+'<p>'+escapeHtml(p.defect)+'</p></div>'+p.reinspections.map(r=>'<div class="timeline"><b>재검사 · '+verdict(r.result)+'</b><p>'+escapeHtml(r.note)+'</p><small>'+escapeHtml(r.operator)+' · '+when(r.createdAt)+'</small></div>').join('')+'<h3>재검사 기록 추가</h3><form id="reinspection"><label>판정<select id="result" required><option value="">선택하세요</option><option value="PASS">합격</option><option value="FAIL">불합격</option></select></label><label>검사자<input id="operator" maxlength="50" required autocomplete="name"></label><label>조치 및 확인 메모<textarea id="note" maxlength="1000" required rows="2"></textarea></label><button class="primary" id="save">기록 저장</button><p class="muted">저장 후 최초 검사와 이전 기록을 함께 보존합니다.</p></form>';
}
async function load() {
  products = await api('/api/inspections');
  selected = products.find(p=>p.id===selected?.id) || products[0];
  renderList(); renderDetail();
}
$('filter').addEventListener('input', renderList);
$('refresh').addEventListener('click',()=>load().catch(showError));
function showError(error) { $('notice').textContent=error.message; }
$('products').addEventListener('click',e=>{
  const button=e.target.closest('[data-id]'); if(!button || busy) return;
  selected=products.find(p=>p.id===button.dataset.id);
  retryRequest=null; $('conversation').replaceChildren(); renderList(); renderDetail();
});
$('detail').addEventListener('submit',async e=>{
  e.preventDefault(); if(busy) return;
  const body={result:$('result').value,operator:$('operator').value,note:$('note').value,expectedRevision:selected.revision};
  const signature=JSON.stringify(body);
  if(!retryRequest || retryRequest.signature!==signature) retryRequest={signature,id:crypto.randomUUID()};
  busy=true; $('save').disabled=true; $('notice').textContent='';
  try { await api('/api/inspections/'+encodeURIComponent(selected.id)+'/reinspections',post({...body,requestId:retryRequest.id})); retryRequest=null; await load(); }
  catch(error){ showError(error); }
  finally {busy=false; if($('save')) $('save').disabled=false;}
});
document.querySelectorAll('[data-question]').forEach(button=>button.addEventListener('click',()=>{
  $('question').value=button.dataset.question; $('question').focus();
}));
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
    const mode=data.mode==='llm'?'AI 근거 선택 · '+data.model:data.mode==='record'?'검사 기록 기준':data.mode==='source-only'?'표준서 원문 · AI 선택 없음':data.mode==='no-evidence'?'근거 없음':'검색 결과 · AI 연결 필요';
    const notice = data.notice ? '<p class="facts">'+escapeHtml(data.notice)+'</p>' : '';
    const answerText = escapeHtml(data.answer).replace(/\[(SOP-\d+)\]/g, '<span class="cite">$1</span>');
    answer.innerHTML='<span class="mode">'+escapeHtml(mode)+'</span>'+notice+'<div class="answer-copy">'+answerText+'</div><div class="facts">'+escapeHtml(data.facts)+'</div>'+data.sources.map(s=>'<details><summary>['+escapeHtml(s.id)+'] '+escapeHtml(s.title)+'</summary><p>'+escapeHtml(s.text)+'</p><small>'+escapeHtml(s.version)+'</small></details>').join('');
    $('conversation').append(answer); $('question').value='';
    $('conversation').scrollTop=$('conversation').scrollHeight;
  } catch(error){showError(error);}
  finally {thinking.remove();busy=false;$('ask').disabled=false;}
});
async function init() {
  await load();
  const documents=await api('/api/documents');
  $('documents').innerHTML=documents.map(s=>'<details><summary>['+escapeHtml(s.id)+'] '+escapeHtml(s.title)+'</summary><p>'+escapeHtml(s.text)+'</p></details>').join('');
  const status=await api('/api/status');
  $('ai-status').textContent=status.aiReady?'AI 연결됨 · '+status.model:'검색 모드 · AI 연결 필요';
  $('ai-status').classList.toggle('is-offline', !status.aiReady);
}
init().catch(showError);
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


