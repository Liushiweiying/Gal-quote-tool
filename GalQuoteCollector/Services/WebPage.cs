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
.filters{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px;margin-bottom:8px}
.filters select{min-width:0}
.filters select{max-width:100%}
.exrow{display:flex;flex-wrap:wrap;gap:6px;align-items:center;margin:0 2px 10px;font-size:12px;color:var(--muted)}
.exrow button.ex{padding:6px 12px;font-size:12px;color:var(--ink2)}
.exrow button.ex.on{background:var(--danger);border-color:var(--danger);color:#fff}
.chk{display:flex;align-items:center;gap:6px;color:var(--muted);font-size:13px;margin:0 2px 10px}
.chk input{width:auto}
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

/* 回想模式 */
#viewer{position:fixed;inset:0;display:none;flex-direction:column;align-items:center;justify-content:center;z-index:90;background:#000}
#viewer.on{display:flex}
#viewer.white{background:#fff}
#viewer img{max-width:100%;max-height:100%;object-fit:contain;flex:1;min-height:0;width:100%}
#viewer #vText{position:absolute;left:0;right:0;bottom:58px;padding:14px 16px;color:#fff;
  background:linear-gradient(to top,rgba(0,0,0,.72),rgba(0,0,0,0));pointer-events:none;max-height:42vh;overflow:hidden}
#viewer.white #vText{color:#1D1D1F;background:linear-gradient(to top,rgba(255,255,255,.92),rgba(255,255,255,0))}
#vGame{font-size:13px;opacity:.85;margin-bottom:4px}
#vQuote{font-size:16px;line-height:1.55;white-space:pre-wrap;word-break:break-word}
#vBar{position:absolute;left:0;right:0;bottom:0;height:58px;display:flex;gap:8px;align-items:center;
  justify-content:center;padding:8px 12px;background:rgba(0,0,0,.55);backdrop-filter:blur(6px)}
#viewer.white #vBar{background:rgba(255,255,255,.86)}
#vBar button{min-width:56px;padding:9px 12px;border-radius:10px;border:1px solid rgba(255,255,255,.25);
  background:rgba(255,255,255,.14);color:#fff;font-size:14px}
#viewer.white #vBar button{background:#fff;border-color:#E5E5EA;color:#1D1D1F}
#vPrev{font-size:20px;line-height:1}
#vPos{position:absolute;top:calc(env(safe-area-inset-top, 0px) + 10px);right:14px;color:#fff;
  font-size:12px;background:rgba(0,0,0,.45);padding:4px 10px;border-radius:999px}
#viewer.white #vPos{color:#1D1D1F;background:rgba(255,255,255,.8)}
@media (max-width:720px){
  .filters{grid-template-columns:minmax(0,1fr)}
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
  header .row button{flex:1 1 44%;padding:10px 6px;font-size:13px}
}

/* ── 电视 / 大屏模式（电视盒子、投影、客厅大屏；遥控器方向键操作） ── */
body.tv{font-size:18px}
body.tv h1{font-size:26px}
body.tv header{padding:14px 22px}
body.tv main{max-width:1600px;padding:18px}
body.tv .filters{gap:14px}
body.tv .card{border-radius:18px;padding:18px 22px;margin-bottom:14px}
body.tv .card .txt{font-size:22px;line-height:1.6}
body.tv .card .meta{font-size:16px}
body.tv .card .notes{font-size:17px}
body.tv .chip{font-size:15px;padding:3px 12px}
body.tv .thumb{max-height:480px}
body.tv button{font-size:18px;padding:12px 20px;border-radius:12px}
body.tv input,body.tv select,body.tv textarea{font-size:18px;padding:12px 14px}
body.tv .acts button{font-size:16px}
body.tv .count,body.tv .chk,body.tv .exrow{font-size:16px}
body.tv .more{padding:16px;font-size:18px}
body.tv #vQuote{font-size:28px}
body.tv #vGame{font-size:19px}
body.tv #vBar{height:82px}
body.tv #vBar button{font-size:19px;min-width:82px;padding:13px 18px}
body.tv #vPos{font-size:16px;padding:6px 14px}
/* 遥控器没有鼠标，焦点必须看得见 */
body.focusnav :focus{outline:4px solid var(--accent);outline-offset:3px}
body.focusnav .card:focus-within{border-color:var(--accent);box-shadow:0 0 0 4px rgba(91,106,191,.25)}
body.focusnav button:focus{background:#EEF0FA;border-color:var(--accent)}
body.focusnav button.primary:focus{background:#4A58A8}
</style>
</head>
<body>
<header>
  <div class="row">
    <h1>语录收藏</h1>
    <button id="btnJson">导出 JSON</button>
    <button id="btnMd">导出 Markdown</button>
    <button id="btnZip">打包导出</button>
    <button id="btnImport">导入</button>
    <button id="btnSlideshow">回想</button>
    <button id="btnTv" title="电视 / 大屏模式：字号加大、可用遥控器方向键操作">大屏：关</button>
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
  <div class="exrow">
    <span>反选（排除选中的）：</span>
    <button class="ex" id="exGame" title="排除选中的游戏">游戏</button>
    <button class="ex" id="exGroup" title="排除选中的分组">分组</button>
    <button class="ex" id="exTag" title="排除选中的标签">标签</button>
  </div>

  <input type="file" id="fileImport" accept=".json,.zip,application/json,application/zip" style="display:none">
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
  <div class="field"><label>新建分组（不存在就创建）</label><input id="dNewGroup" placeholder="例如：共通线"></div>
  <div class="field"><label>新建标签（不存在就创建）</label><input id="dNewTag" placeholder="例如：名场面"></div>
  <div class="row" style="margin-top:14px;justify-content:flex-end">
    <button class="danger" id="dDelete">删除</button>
    <button id="dCancel">取消</button>
    <button class="primary" id="dSave">保存</button>
  </div>
</div></div>

<div id="zoom"><img id="zoomImg" alt=""></div>

<!-- 回想模式（手机/电脑都能用）：全屏看图 + 上一条/下一条 + 自动播放 + 黑边处理（不改文件） -->
<div id="viewer" class="white">
  <img id="vImg" alt="">
  <div id="vText"><div id="vGame"></div><div id="vQuote"></div></div>
  <div id="vBar">
    <button id="vPrev">‹</button>
    <button id="vPlay">▶ 自动</button>
    <button id="vBars">原样</button>
    <button id="vClose">×</button>
  </div>
  <div id="vPos"></div>
</div>
<div id="toast"></div>

<script>
const S={q:'',game:'',group:'',tag:'',offset:0,limit:20,total:0,meta:null,edit:null,canEdit:true,
         ex:{game:false,group:false,tag:false}};
const $=id=>document.getElementById(id);
const esc=s=>(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
function toast(msg){const t=$('toast');t.textContent=msg;t.classList.add('on');clearTimeout(t._h);t._h=setTimeout(()=>t.classList.remove('on'),1800);}
async function api(path,opt){const r=await fetch(path,opt||{});if(!r.ok)throw new Error(await r.text()||('HTTP '+r.status));const ct=r.headers.get('content-type')||'';return ct.includes('json')?r.json():r.text();}
function fmt(t){return (t||'').replace('T',' ').slice(0,16);}

async function loadMeta(){
  S.meta=await api('/api/meta');
  S.canEdit=(S.meta.canEdit!==false);
  if(!S.canEdit){
    const why=S.meta.isLan?'只读模式（这台设备设置为「只能看」）':'外网访问：只读模式';
    document.body.insertAdjacentHTML('afterbegin','<div style="padding:10px 14px;background:#FFF4E5;color:#8A5A00;font-size:13px">'+why+'（可以查看、搜索、导出；修改请在电脑上操作）</div>');
  }
  // 只读时把「导入」藏掉（服务端本来也会 403，这里只是别让人看见）
  const bi=$('btnImport');if(bi)bi.style.display=S.canEdit?'':'none';
  const fill=(el,arr,val,fmtf)=>{el.innerHTML='<option value="">'+val+'</option>'+arr.map(x=>`<option value="${esc(fmtf?fmtf(x):x)}">${esc(fmtf?fmtf(x):x)}</option>`).join('');};
  fill($('fGame'),S.meta.games,'全部游戏');
  $('fGroup').innerHTML='<option value="">全部分组</option>'+S.meta.groups.map(g=>`<option value="${g.id}">${esc(g.name)}</option>`).join('');
  $('fTag').innerHTML='<option value="">全部标签</option>'+S.meta.tags.map(t=>`<option value="${t.id}">${esc(t.name)}</option>`).join('');
  $('games').innerHTML=S.meta.games.map(g=>`<option value="${esc(g)}">`).join('');
}

const UNKNOWN='[未识别到文字]';
function card(it){
  const body=(it.text||'').includes(UNKNOWN)?'':it.text;
  const chips=[...(it.groups||[]).map(g=>`<span class="chip">${esc(g)}</span>`),...(it.tags||[]).map(t=>`<span class="chip">#${esc(t)}</span>`)].join(' ');
  // 可编辑时才给「编辑 / 删除」入口（公网只读、只读镜像模式下服务端也会 403，这里直接不显示）
  const editActs=S.canEdit?`<button data-act="edit">编辑</button><button class="danger" data-act="del">删除</button>`:'';
  return `<div class="card" data-id="${it.id}">
    ${body?`<div class="txt">${esc(body)}</div>`:''}
    ${it.notes?`<div class="notes">${esc(it.notes)}</div>`:''}
    ${it.hasShot?`<img class="thumb" loading="lazy" src="/api/shot/${it.id}" alt="">`:''}
    <div class="meta"><span class="game">${esc(it.gameName||'未标注')}</span><span>${fmt(it.capturedAt)}</span>${chips}</div>
    <div class="acts"><button data-act="exp">导出JSON</button><button data-act="expzip">导出ZIP</button>${editActs}</div>
  </div>`;
}

async function load(reset){
  if(reset){S.offset=0;$('list').innerHTML='';}
  const ex=Object.keys(S.ex).filter(k=>S.ex[k]).join(',');
  const p=new URLSearchParams({q:S.q,game:S.game,group:S.group,tag:S.tag,offset:S.offset,limit:S.limit,ex:ex});
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
  $('dNewGroup').value='';$('dNewTag').value='';
  picker($('dGroups'),S.meta.groups.map(g=>g.name),it.groups||[]);
  picker($('dTags'),S.meta.tags.map(t=>t.name),it.tags||[]);
  $('mask').classList.add('on');
}
function closeEdit(){$('mask').classList.remove('on');S.edit=null;}

async function save(){
  const it=S.edit;if(!it)return;
  const splitNames=v=>(v||'').split(/[,，]/).map(s=>s.trim()).filter(Boolean);
  const body={text:$('dText').value,gameName:$('dGame').value,notes:$('dNotes').value,
    capturedAt:$('dTime').value,groups:picked($('dGroups')),tags:picked($('dTags')),
    newGroups:splitNames($('dNewGroup').value),newTags:splitNames($('dNewTag').value)};
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
  else if(e.target.dataset.act==='exp'){location.href='/api/quotes/'+id+'/export';}
  else if(e.target.dataset.act==='expzip'){location.href='/api/quotes/'+id+'/export-zip';}
});
$('list').addEventListener('click',e=>{if(e.target.classList.contains('thumb')){const c=e.target.closest('.card');openViewer(c?+c.dataset.id:0);}});
$('zoom').onclick=()=>$('zoom').classList.remove('on');
$('more').onclick=()=>load(false);
$('dSave').onclick=save;$('dCancel').onclick=closeEdit;
$('dDelete').onclick=()=>{if(S.edit)del(S.edit.id);};
$('mask').onclick=e=>{if(e.target.id==='mask')closeEdit();};
$('btnReload').onclick=()=>load(true);
$('btnJson').onclick=()=>location.href='/api/export?format=json';
$('btnMd').onclick=()=>location.href='/api/export?format=md';
$('btnZip').onclick=()=>location.href='/api/export-zip';

['game','group','tag'].forEach(k=>{
  const btn=$('ex'+k[0].toUpperCase()+k.slice(1));
  btn.onclick=()=>{S.ex[k]=!S.ex[k];btn.classList.toggle('on',S.ex[k]);btn.title=(S.ex[k]?'取消排除':'排除选中的')+({game:'游戏',group:'分组',tag:'标签'}[k]);load(true);};
});
$('btnImport').onclick=()=>$('fileImport').click();
$('btnSlideshow').onclick=()=>openViewer(0);
$('fileImport').onchange=async e=>{
  const f=e.target.files[0];if(!f)return;
  try{
    const text=await f.text();
    const res=await api('/api/import',{method:'POST',headers:{'Content-Type':'application/json'},body:text});
    toast('已导入 #'+res.id);await loadMeta();await load(true);
  }catch(err){toast('导入失败：'+err.message);}
  e.target.value='';
};
let deb;const onSearch=()=>{clearTimeout(deb);deb=setTimeout(()=>{S.q=$('q').value.trim();load(true);},250);};

/* ── 回想模式 ── */
const V={list:[],i:0,bars:0,timer:null,timerOn:false};
async function openViewer(startId){
  const ex=Object.keys(S.ex).filter(k=>S.ex[k]).join(',');
  const p=new URLSearchParams({q:S.q,game:S.game,group:S.group,tag:S.tag,offset:0,limit:200,ex:ex});
  const data=await api('/api/quotes?'+p);
  V.list=data.items.filter(x=>x.hasShot||x.text);
  if(!V.list.length){toast('没有可回想的语录');return;}
  V.i=Math.max(0,V.list.findIndex(x=>x.id===startId));
  document.addEventListener('keydown',onViewerKey);
  $('viewer').classList.add('on');
  showSlide();
}
function closeViewer(){
  $('viewer').classList.remove('on');
  stopAuto();
  document.removeEventListener('keydown',onViewerKey);
}
function showSlide(){
  const it=V.list[V.i];if(!it)return;
  $('vImg').src=it.hasShot?('/api/shot/'+it.id+'?bars='+V.bars):'';
  $('vImg').style.display=it.hasShot?'block':'none';
  $('vQuote').textContent=(it.text||'').includes(UNKNOWN)?'':it.text;
  $('vGame').textContent=(it.gameName||'')+(it.capturedAt?('　'+it.capturedAt.replace('T',' ').slice(0,16)):'');
  $('vPos').textContent=(V.i+1)+' / '+V.list.length;
  $('vBars').textContent=['原样','裁掉黑边','黑边涂白'][V.bars];
  $('viewer').classList.toggle('white',V.bars!==0);
  preload(V.i+1);preload(V.i-1);   // 电视盒子解码慢，提前把前后一张拉下来
}
function preload(i){
  if(!V.list.length)return;
  const it=V.list[(i+V.list.length)%V.list.length];
  if(!it||!it.hasShot)return;
  const im=new Image();im.src='/api/shot/'+it.id+'?bars='+V.bars;
}
const vNext=d=>{if(!V.list.length)return;V.i=(V.i+d+V.list.length)%V.list.length;showSlide();};
function startAuto(){V.timer=setInterval(()=>vNext(1),5000);V.timerOn=true;$('vPlay').textContent='⏸ 暂停';}
function stopAuto(){if(V.timer)clearInterval(V.timer);V.timer=null;V.timerOn=false;if($('vPlay'))$('vPlay').textContent='▶ 自动';}
function onViewerKey(e){
  if(e.key==='ArrowRight'||e.key===' '){vNext(1);e.preventDefault();}
  else if(e.key==='ArrowLeft'){vNext(-1);e.preventDefault();}
  else if(e.key==='ArrowDown'){vNext(1);e.preventDefault();}      // 遥控器上/下也翻页
  else if(e.key==='ArrowUp'){vNext(-1);e.preventDefault();}
  else if(e.key==='Enter'){V.timerOn?stopAuto():startAuto();e.preventDefault();}  // 遥控器 OK 键 = 自动播放开关
  else if(e.key==='b'||e.key==='B'){V.bars=(V.bars+1)%3;showSlide();}
  else if(e.key==='Escape'||e.key==='Backspace'){closeViewer();}
}
$('vPrev').onclick=e=>{e.stopPropagation();vNext(-1);};
$('vPlay').onclick=e=>{e.stopPropagation();V.timerOn?stopAuto():startAuto();};
$('vBars').onclick=e=>{e.stopPropagation();V.bars=(V.bars+1)%3;showSlide();};
$('vClose').onclick=e=>{e.stopPropagation();closeViewer();};
$('viewer').onclick=e=>{if(e.target.closest('#vBar'))return;vNext(e.clientX<window.innerWidth*0.35?-1:1);};
let vTouchX=null;
$('viewer').addEventListener('touchstart',e=>{vTouchX=e.changedTouches[0].clientX;},{passive:true});
$('viewer').addEventListener('touchend',e=>{
  if(vTouchX===null)return;const dx=e.changedTouches[0].clientX-vTouchX;vTouchX=null;
  if(Math.abs(dx)>50){vNext(dx<0?1:-1);}
},{passive:true});
$('q').oninput=onSearch;
$('fGame').onchange=()=>{S.game=$('fGame').value;load(true);};
$('fGroup').onchange=()=>{S.group=$('fGroup').value;load(true);};
$('fTag').onchange=()=>{S.tag=$('fTag').value;load(true);};
document.addEventListener('keydown',e=>{if(e.key==='Escape'){closeEdit();$('zoom').classList.remove('on');}});

/* ── 电视 / 大屏模式 + 遥控器方向键导航 ──
   电视盒子（Android TV / 各种 TV 浏览器）没有鼠标，方向键会变成 ArrowUp/Down/Left/Right，
   OK 键 = Enter。原生焦点在按钮上时 Enter 会直接激活，所以只要把"焦点移动"补齐就能用遥控器操作。 */
const TV={on:false};
function detectTv(){
  const q=new URLSearchParams(location.search).get('tv');
  if(q==='1'||q==='0')return q==='1';
  try{const saved=localStorage.getItem('galqt-tv');if(saved==='1'||saved==='0')return saved==='1';}catch(e){}
  const ua=navigator.userAgent||'';
  if(/Android ?TV|GoogleTV|SmartTV|SMART-TV|BRAVIA|AFT[BMN]|MiBOX|MiTV|HUAWEI ?TV|TV ?Browser|NetCast|Tizen|Web0S|AppleTV|Xbox/i.test(ua))return true;
  return window.innerWidth>=1400&&window.innerHeight>=700&&!window.matchMedia('(pointer:fine)').matches;
}
function applyTv(){
  document.body.classList.toggle('tv',TV.on);
  document.body.classList.toggle('focusnav',TV.on);
  const b=$('btnTv');if(b)b.textContent=TV.on?'大屏：开':'大屏：关';
}
$('btnTv').onclick=()=>{TV.on=!TV.on;try{localStorage.setItem('galqt-tv',TV.on?'1':'0');}catch(e){}applyTv();};

function focusables(){
  return [...document.querySelectorAll('button,input,select,textarea,[href]')].filter(el=>
    !el.disabled&&el.offsetParent!==null&&el.getAttribute('aria-hidden')!=='true');
}
function moveFocus(dir){
  const list=focusables();if(!list.length)return false;
  const cur=list.includes(document.activeElement)?document.activeElement:null;
  if(!cur){list[0].focus();return true;}
  const a=cur.getBoundingClientRect(),ax=a.left+a.width/2,ay=a.top+a.height/2;
  let best=null,bestScore=Infinity;
  for(const el of list){
    if(el===cur)continue;
    const r=el.getBoundingClientRect();
    const dx=(r.left+r.width/2)-ax,dy=(r.top+r.height/2)-ay;
    let primary,secondary;
    if(dir==='down'){primary=dy;secondary=dx;}
    else if(dir==='up'){primary=-dy;secondary=dx;}
    else if(dir==='right'){primary=dx;secondary=dy;}
    else{primary=-dx;secondary=dy;}
    if(primary<4)continue;                       // 不在这个方向上的直接跳过
    const score=primary+Math.abs(secondary)*2.5; // 偏得越多分越高（优先正前方）
    if(score<bestScore){bestScore=score;best=el;}
  }
  if(!best)return false;
  best.focus();
  try{best.scrollIntoView({block:'center'});}catch(e){best.scrollIntoView();}
  return true;
}
document.addEventListener('keydown',e=>{
  if($('viewer').classList.contains('on'))return;   // 回想模式有自己的按键处理
  if($('mask').classList.contains('on'))return;     // 编辑弹窗里用原生 Tab
  const tag=(document.activeElement&&document.activeElement.tagName)||'';
  const inText=tag==='INPUT'||tag==='TEXTAREA'||tag==='SELECT';
  const k=e.key;
  if(inText){
    // 输入框里左右键要移动光标；只有"下"才离开输入框（电视上很自然）
    if(k==='ArrowDown'&&moveFocus('down')){e.preventDefault();document.activeElement.blur();}
    return;
  }
  if(k==='ArrowDown'&&moveFocus('down'))e.preventDefault();
  else if(k==='ArrowUp'&&moveFocus('up'))e.preventDefault();
  else if(k==='ArrowRight'&&moveFocus('right'))e.preventDefault();
  else if(k==='ArrowLeft'&&moveFocus('left'))e.preventDefault();
});

(async()=>{
  TV.on=detectTv();applyTv();
  try{await loadMeta();await load(true);
    // ?view=1 → 直接进回想（手机/电视可以收藏这个链接）；&auto=1 → 进去就自动播放
    if(/[?&]view=1/.test(location.search))setTimeout(async()=>{await openViewer(0);
      if(/[?&]auto=1/.test(location.search))startAuto();},300);
  }catch(e){document.body.insertAdjacentHTML('afterbegin','<div style="padding:12px;color:#D9534F">加载失败：'+esc(e.message)+'</div>');}})();
</script>
</body>
</html>
""";

    /// <summary>
    /// 登录页（公网访问时用）：要访问码和/或 6 位动态码。
    /// 特意做得又大又简单——手机和电视遥控器都好操作（数字键盘、自动聚焦、Enter 直接提交）。
    /// </summary>
    public static string Login(bool needCode, bool needTotp, bool failed)
    {
        var codeField = needCode
            ? """<div class="f"><label for="k">访问码</label><input id="k" name="k" type="password" autocomplete="current-password" placeholder="设置里那个访问码"></div>"""
            : "";
        var totpField = needTotp
            ? """<div class="f"><label for="code">动态码（6 位）</label><input id="code" name="code" class="otp" inputmode="numeric" autocomplete="one-time-code" maxlength="6" placeholder="000000"></div>"""
            : "";
        var focusId = needTotp ? "code" : (needCode ? "k" : "");
        var fail = failed
            ? """<div class="err">访问码或动态码不对，再试一次。（连续错 8 次会暂时封锁）</div>"""
            : "";
        var hint = needTotp
            ? "打开手机上的验证器 App（Microsoft / Google Authenticator、Aegis 等），输入「Gal Quote Tool」那一条的 6 位数字。验证一次后 12 小时内不用再输。"
            : "输入设置里「网页与手机」页填的访问码，之后这个浏览器会记住。";
        return $$"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#1D1D1F">
<title>登录 · 语录收藏</title>
<style>
*{box-sizing:border-box}
body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;padding:20px;
  background:linear-gradient(160deg,#1D1D1F,#3A3A5C);color:#1D1D1F;
  font:16px/1.6 -apple-system,BlinkMacSystemFont,"Segoe UI","Microsoft YaHei",sans-serif}
.card{background:#fff;border-radius:20px;padding:26px 24px;width:min(430px,100%);box-shadow:0 18px 60px rgba(0,0,0,.35)}
h1{margin:0 0 4px;font-size:22px}
p.sub{margin:0 0 18px;color:#8E8E93;font-size:14px}
.f{margin-bottom:14px}
label{display:block;font-size:14px;color:#3C3C43;margin-bottom:6px}
input{width:100%;font:inherit;font-size:18px;padding:14px 16px;border:1px solid #E5E5EA;border-radius:14px;background:#F7F7FA}
input:focus{outline:3px solid #5B6ABF;outline-offset:1px;background:#fff}
input.otp{letter-spacing:.42em;font-size:30px;text-align:center;font-weight:600;padding:12px 10px}
button{width:100%;font:inherit;font-size:18px;font-weight:600;padding:15px;border:0;border-radius:14px;
  background:#5B6ABF;color:#fff;cursor:pointer;margin-top:6px}
button:focus{outline:3px solid #1D1D1F;outline-offset:2px}
.err{background:#FFF1F0;color:#B3261E;border-radius:12px;padding:10px 12px;font-size:14px;margin-bottom:14px}
.hint{color:#8E8E93;font-size:13px;margin:16px 0 0;line-height:1.55}
</style>
</head>
<body>
<form class="card" method="post" action="/login">
  <h1>语录收藏</h1>
  <p class="sub">{{(needTotp ? "这台设备来自公网：需要两步验证" : "需要访问码")}}</p>
  {{fail}}
  {{codeField}}
  {{totpField}}
  <button type="submit">进入</button>
  <p class="hint">{{hint}}</p>
</form>
<script>var el=document.getElementById('{{focusId}}');if(el)el.focus();</script>
</body>
</html>
""";
    }

    /// <summary>纯提示页（例如「未开放公网访问」）。</summary>
    public static string Notice(string title, string message) => $$"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{title}}</title>
<style>
body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;padding:20px;
  background:#F2F2F7;color:#1D1D1F;font:16px/1.6 -apple-system,BlinkMacSystemFont,"Segoe UI","Microsoft YaHei",sans-serif}
.card{background:#fff;border-radius:18px;padding:24px;width:min(520px,100%);box-shadow:0 8px 30px rgba(0,0,0,.08)}
h1{margin:0 0 10px;font-size:20px}
p{margin:0;color:#3C3C43;white-space:pre-wrap}
</style>
</head>
<body><div class="card"><h1>{{title}}</h1><p>{{message}}</p></div></body>
</html>
""";
}
