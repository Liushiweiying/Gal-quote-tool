namespace GalQuoteCollector.Services;

/// <summary>
/// 内网网页（手机/电脑都能开）：查看、搜索、修改、导出语录。
/// 单文件单页，样式和脚本全部内嵌（内网可能没外网，不能用 CDN）。
/// </summary>
public static class WebPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#5B6ABF">
<title>语录收藏</title>
<style>
:root{--accent:#5B6ABF;--bg:#F2F2F7;--card:#fff;--ink:#1D1D1F;--ink2:#3C3C43;--muted:#8E8E93;--line:#E5E5EA;--danger:#D9534F}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.6 -apple-system,BlinkMacSystemFont,"Segoe UI","Microsoft YaHei",sans-serif}
header{position:sticky;top:0;z-index:10;background:rgba(242,242,247,.92);backdrop-filter:blur(12px);border-bottom:1px solid var(--line);padding:10px 14px}
.row{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
h1{font-size:19px;margin:0;flex:1;white-space:nowrap}
input,select,textarea,button{font:inherit;color:inherit}
input,select,textarea{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:9px 11px;width:100%}
textarea{min-height:96px;resize:vertical;line-height:1.5}
button{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:9px 13px;cursor:pointer;white-space:nowrap}
button.primary{background:var(--accent);border-color:var(--accent);color:#fff}
button.danger{color:var(--danger)}
button:disabled{opacity:.5}
main{padding:12px;max-width:1100px;margin:0 auto}
.filters{display:grid;grid-template-columns:repeat(3,1fr);gap:8px;margin-bottom:10px}
.count{color:var(--muted);font-size:13px;margin:2px 2px 8px}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:12px 14px;margin-bottom:10px}
.card .txt{white-space:pre-wrap;word-break:break-word;font-size:15px}
.card .meta{display:flex;gap:8px;flex-wrap:wrap;align-items:center;color:var(--muted);font-size:12px;margin-top:8px}
.card .game{color:var(--accent);font-weight:600}
.chip{background:#EEF0FA;color:#46508F;border-radius:999px;padding:2px 9px;font-size:12px}
.card .notes{color:var(--ink2);font-size:13px;margin-top:6px;white-space:pre-wrap}
.thumb{max-width:100%;max-height:280px;object-fit:cover;object-position:top;border-radius:10px;margin-top:10px;display:block;cursor:zoom-in;border:1px solid var(--line)}
.acts{display:flex;gap:8px;margin-top:10px}
.acts button{padding:7px 12px;font-size:13px}
.more{width:100%;margin:6px 0 40px}
#mask{position:fixed;inset:0;background:rgba(0,0,0,.45);display:none;align-items:center;justify-content:center;z-index:50;padding:16px}
#mask.on{display:flex}
#dialog{background:var(--card);border-radius:16px;width:min(720px,100%);max-height:92vh;overflow:auto;padding:16px}
#dialog h2{margin:2px 0 12px;font-size:17px}
.field{margin-bottom:10px}
.field label{display:block;font-size:12px;color:var(--muted);margin-bottom:4px}
.picker{display:flex;flex-wrap:wrap;gap:6px}
.picker button{padding:6px 11px;font-size:13px;border-radius:999px}
.picker button.on{background:var(--accent);border-color:var(--accent);color:#fff}
#toast{position:fixed;left:50%;bottom:22px;transform:translateX(-50%);background:#1D1D1F;color:#fff;padding:9px 16px;border-radius:999px;font-size:13px;opacity:0;transition:opacity .2s;pointer-events:none;z-index:99;max-width:90vw}
#toast.on{opacity:.95}
#zoom{position:fixed;inset:0;background:rgba(0,0,0,.9);display:none;align-items:center;justify-content:center;z-index:80;padding:10px}
#zoom.on{display:flex}
#zoom img{max-width:100%;max-height:100%}
@media (max-width:720px){
  .filters{grid-template-columns:1fr}
  main{padding:10px}
  #mask{padding:0;align-items:flex-end}
  #dialog{border-radius:16px 16px 0 0;max-height:94vh}
  .card{padding:12px}
  .thumb{max-height:200px}
  button{padding:11px 14px}
}
@media (max-width:520px){
  h1{flex:1 0 100%;margin-bottom:2px}
  header .row{flex-wrap:wrap}
  header .row button{flex:1 1 30%;padding:10px 6px;font-size:13px}
}
</style>
</head>
<body>
<header>
  <div class="row">
    <h1>语录收藏</h1>
    <button id="btnJson">导出 JSON</button>
    <button id="btnMd">导出 Markdown</button>
    <button id="btnReload">刷新</button>
  </div>
  <div class="row" style="margin-top:8px">
    <input id="q" placeholder="搜索正文 / 游戏名 / 备注…" style="flex:1;min-width:180px">
  </div>
</header>
<main>
  <div class="filters">
    <select id="fGame"><option value="">全部游戏</option></select>
    <select id="fGroup"><option value="">全部分组</option></select>
    <select id="fTag"><option value="">全部标签</option></select>
  </div>
  <div class="count" id="count"></div>
  <div id="list"></div>
  <button class="more" id="more" style="display:none">加载更多</button>
</main>

<div id="mask"><div id="dialog">
  <h2 id="dTitle">编辑语录</h2>
  <div class="field"><label>正文</label><textarea id="dText"></textarea></div>
  <div class="field"><label>游戏名</label><input id="dGame" list="games"><datalist id="games"></datalist></div>
  <div class="field"><label>备注</label><textarea id="dNotes" style="min-height:64px"></textarea></div>
  <div class="field"><label>时间</label><input id="dTime" type="datetime-local" step="1"></div>
  <div class="field"><label>分组</label><div class="picker" id="dGroups"></div></div>
  <div class="field"><label>标签</label><div class="picker" id="dTags"></div></div>
  <div class="field"><label>新建分组 / 标签（逗号分隔）</label><input id="dNew" placeholder="例如：共通线, 名场面"></div>
  <div class="row" style="margin-top:14px;justify-content:flex-end">
    <button class="danger" id="dDelete">删除</button>
    <button id="dCancel">取消</button>
    <button class="primary" id="dSave">保存</button>
  </div>
</div></div>

<div id="zoom"><img id="zoomImg" alt=""></div>
<div id="toast"></div>

<script>
const S={q:'',game:'',group:'',tag:'',offset:0,limit:20,total:0,meta:null,edit:null};
const $=id=>document.getElementById(id);
const esc=s=>(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
function toast(msg){const t=$('toast');t.textContent=msg;t.classList.add('on');clearTimeout(t._h);t._h=setTimeout(()=>t.classList.remove('on'),1800);}
async function api(path,opt){const r=await fetch(path,opt||{});if(!r.ok)throw new Error(await r.text()||('HTTP '+r.status));const ct=r.headers.get('content-type')||'';return ct.includes('json')?r.json():r.text();}
function fmt(t){return (t||'').replace('T',' ').slice(0,16);}

async function loadMeta(){
  S.meta=await api('/api/meta');
  const fill=(el,arr,val,fmtf)=>{el.innerHTML='<option value="">'+val+'</option>'+arr.map(x=>`<option value="${esc(fmtf?fmtf(x):x)}">${esc(fmtf?fmtf(x):x)}</option>`).join('');};
  fill($('fGame'),S.meta.games,'全部游戏');
  $('fGroup').innerHTML='<option value="">全部分组</option>'+S.meta.groups.map(g=>`<option value="${g.id}">${esc(g.name)}</option>`).join('');
  $('fTag').innerHTML='<option value="">全部标签</option>'+S.meta.tags.map(t=>`<option value="${t.id}">${esc(t.name)}</option>`).join('');
  $('games').innerHTML=S.meta.games.map(g=>`<option value="${esc(g)}">`).join('');
}

function card(it){
  const chips=[...(it.groups||[]).map(g=>`<span class="chip">${esc(g)}</span>`),...(it.tags||[]).map(t=>`<span class="chip">#${esc(t)}</span>`)].join(' ');
  return `<div class="card" data-id="${it.id}">
    <div class="txt">${esc(it.text)}</div>
    ${it.notes?`<div class="notes">${esc(it.notes)}</div>`:''}
    ${it.hasShot?`<img class="thumb" loading="lazy" src="/api/shot/${it.id}" alt="">`:''}
    <div class="meta"><span class="game">${esc(it.gameName||'未标注')}</span><span>${fmt(it.capturedAt)}</span>${chips}</div>
    <div class="acts"><button data-act="edit">编辑</button><button data-act="del" class="danger">删除</button></div>
  </div>`;
}

async function load(reset){
  if(reset){S.offset=0;$('list').innerHTML='';}
  const p=new URLSearchParams({q:S.q,game:S.game,group:S.group,tag:S.tag,offset:S.offset,limit:S.limit});
  const data=await api('/api/quotes?'+p);
  S.total=data.total;
  $('count').textContent=`共 ${data.total} 条${data.items.length?`，已显示 ${Math.min(S.offset+data.items.length,data.total)} 条`:''}`;
  $('list').insertAdjacentHTML('beforeend',data.items.map(card).join(''));
  S.offset+=data.items.length;
  $('more').style.display=S.offset<S.total?'block':'none';
  if(!data.total)$('list').innerHTML='<div class="card" style="color:#8E8E93">没有匹配的语录</div>';
}

function picker(el,all,selected){
  el.innerHTML=(all||[]).map((name,i)=>`<button type="button" data-name="${esc(name)}" class="${selected.includes(name)?'on':''}">${esc(name)}</button>`).join('');
  el.querySelectorAll('button').forEach(b=>b.onclick=()=>b.classList.toggle('on'));
}
function picked(el){return [...el.querySelectorAll('button.on')].map(b=>b.dataset.name);}

function openEdit(it){
  S.edit=it;
  $('dTitle').textContent='编辑语录 #'+it.id;
  $('dText').value=it.text||'';$('dGame').value=it.gameName||'';$('dNotes').value=it.notes||'';
  $('dTime').value=(it.capturedAt||'').slice(0,19);
  $('dNew').value='';
  picker($('dGroups'),S.meta.groups.map(g=>g.name),it.groups||[]);
  picker($('dTags'),S.meta.tags.map(t=>t.name),it.tags||[]);
  $('mask').classList.add('on');
}
function closeEdit(){$('mask').classList.remove('on');S.edit=null;}

async function save(){
  const it=S.edit;if(!it)return;
  const body={text:$('dText').value,gameName:$('dGame').value,notes:$('dNotes').value,
    capturedAt:$('dTime').value,groups:picked($('dGroups')),tags:picked($('dTags')),
    newNames:($('dNew').value||'').split(/[,，]/).map(s=>s.trim()).filter(Boolean)};
  try{await api('/api/quotes/'+it.id,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    toast('已保存');closeEdit();await loadMeta();await load(true);}catch(e){toast('保存失败：'+e.message);}
}
async function del(id){
  if(!confirm('删除这条语录？不可撤销。'))return;
  try{await api('/api/quotes/'+id,{method:'DELETE'});toast('已删除');closeEdit();await load(true);}catch(e){toast('删除失败：'+e.message);}
}

$('list').addEventListener('click',async e=>{
  const cardEl=e.target.closest('.card');if(!cardEl)return;
  const id=+cardEl.dataset.id;
  if(e.target.dataset.act==='edit'){
    try{openEdit(await api('/api/quotes/'+id));}catch(err){toast('打开失败');}
  }else if(e.target.dataset.act==='del'){del(id);}
});
$('list').addEventListener('click',e=>{if(e.target.classList.contains('thumb')){$('zoomImg').src=e.target.src;$('zoom').classList.add('on');}});
$('zoom').onclick=()=>$('zoom').classList.remove('on');
$('more').onclick=()=>load(false);
$('dSave').onclick=save;$('dCancel').onclick=closeEdit;
$('dDelete').onclick=()=>{if(S.edit)del(S.edit.id);};
$('mask').onclick=e=>{if(e.target.id==='mask')closeEdit();};
$('btnReload').onclick=()=>load(true);
$('btnJson').onclick=()=>location.href='/api/export?format=json';
$('btnMd').onclick=()=>location.href='/api/export?format=md';
let deb;const onSearch=()=>{clearTimeout(deb);deb=setTimeout(()=>{S.q=$('q').value.trim();load(true);},250);};
$('q').oninput=onSearch;
$('fGame').onchange=()=>{S.game=$('fGame').value;load(true);};
$('fGroup').onchange=()=>{S.group=$('fGroup').value;load(true);};
$('fTag').onchange=()=>{S.tag=$('fTag').value;load(true);};
document.addEventListener('keydown',e=>{if(e.key==='Escape'){closeEdit();$('zoom').classList.remove('on');}});
(async()=>{try{await loadMeta();await load(true);}catch(e){document.body.insertAdjacentHTML('afterbegin','<div style="padding:12px;color:#D9534F">加载失败：'+esc(e.message)+'</div>');}})();
</script>
</body>
</html>
""";
}
