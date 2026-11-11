from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
HELP = ROOT / "Breeze" / "BreezeHelp.html"
STANDALONE = ROOT / "Breeze" / "BreezeHelp.standalone.html"

STYLE = r"""
<!-- MODERN-HELP-STYLE-START -->
<style id="modern-help-style">
  :root{--help-nav:#143653;--help-teal:#126763;--help-paper:#fff;--help-bg:#e8edf2;--help-line:#d7e0e7;--help-ink:#18232d}
  html{scroll-behavior:smooth;scroll-padding-top:18px}body{margin:0!important;background:var(--help-bg)!important;color:var(--help-ink)!important}
  #modern-help-nav{position:fixed;inset:0 auto 0 0;width:292px;padding:16px 13px;background:linear-gradient(165deg,var(--help-nav),#102a42 65%,#123a34);color:#fff;overflow:auto;z-index:9999;box-shadow:3px 0 18px #10243835}
  #modern-help-nav *{box-sizing:border-box}#modern-help-nav .mh-brand{padding:3px 7px 13px;border-bottom:1px solid #ffffff2b}#modern-help-nav .mh-brand b{display:block;font-size:21px}#modern-help-nav .mh-brand span{color:#c7d9e5;font-size:12px}
  #modern-help-state{margin:12px 5px 9px;padding:9px 10px;border-radius:8px;background:#ffffff14;font-weight:700}#modern-help-search{width:calc(100% - 10px);margin:0 5px 10px;padding:8px 9px;border:1px solid #ffffff3d;border-radius:7px;background:#ffffff12;color:#fff;outline:none}#modern-help-search::placeholder{color:#c7d3dc}
  #modern-help-toc{display:grid;gap:2px}#modern-help-toc a{display:block;padding:7px 8px;border-radius:6px;color:#dce9f1;text-decoration:none;line-height:1.25}#modern-help-toc a.mh-sub{padding-left:18px;font-size:12px;color:#c5d7e3}#modern-help-toc a:hover{background:#ffffff13}#modern-help-toc a.active{background:#ecf8f6;color:#143d3a;font-weight:700}
  #modern-help-nav .mh-actions{position:sticky;bottom:-16px;display:flex;gap:6px;margin:12px -13px -16px;padding:10px 18px 15px;background:#12342f}#modern-help-nav button{flex:1;padding:7px;border:1px solid #ffffff3b;border-radius:6px;background:#ffffff12;color:#fff;cursor:pointer}
  body>main{width:auto!important;max-width:none!important;margin:0 0 0 292px!important;padding:22px 28px 52px!important;background:transparent!important;box-shadow:none!important}
  .mh-page{position:relative;width:min(210mm,calc(100vw - 360px));height:297mm;margin:0 auto 28px!important;padding:16mm 16mm 19mm!important;background:var(--help-paper)!important;box-shadow:0 5px 22px #20364b24!important;overflow:hidden}
  .mh-page:before{content:"MARKET BREEZE · СПРАВКА";position:absolute;top:7mm;right:16mm;color:#8b98a4;font-size:9px;letter-spacing:.11em}.mh-page>.mh-continuation{margin:0 0 5mm;color:#68808f;font-size:11px;text-transform:uppercase;letter-spacing:.08em}
  .mh-footer{position:absolute;left:16mm;right:16mm;bottom:7mm;padding-top:4mm;border-top:1px solid var(--help-line);display:flex;justify-content:space-between;color:#7a8792;font-size:10px}.mh-oversize{height:auto!important;min-height:297mm;overflow:visible!important}.mh-anchor{display:block;position:relative;top:-12px}.neural-full{border:0!important;background:transparent!important;padding:0!important}.neural-full>summary{display:none}.neural-reference{margin:0!important}
  .mh-page h1{line-height:1.12}.mh-page h2{scroll-margin-top:18px}.mh-page table{break-inside:auto}.mh-page tr{break-inside:avoid}.mh-page img{max-width:100%;height:auto}.mh-hidden{display:none!important}
  @media(max-width:900px){#modern-help-nav{position:relative;width:auto;max-height:none}body>main{margin:0!important;padding:10px!important}.mh-page{width:100%;height:auto;min-height:0;margin-bottom:12px!important;padding:18px 16px 48px!important;overflow:visible}.mh-page:before{display:none}.mh-footer{left:16px;right:16px;bottom:12px}}
  @media print{@page{size:A4;margin:0}#modern-help-nav{display:none!important}body>main{margin:0!important;padding:0!important}.mh-page{width:210mm!important;min-height:297mm!important;margin:0!important;padding:16mm 16mm 19mm!important;box-shadow:none!important;break-after:page;page-break-after:always}.mh-page:last-child{break-after:auto;page-break-after:auto}.mh-footer{left:16mm;right:16mm;bottom:7mm}}
</style>
<!-- MODERN-HELP-STYLE-END -->
"""

NAV = r"""
<!-- MODERN-HELP-NAV-START -->
<aside id="modern-help-nav">
  <div class="mh-brand"><b>Market Breeze</b><span>Полная справка · Wealth-Lab 8</span></div>
  <div id="modern-help-state">Подготовка страниц…</div>
  <input id="modern-help-search" type="search" placeholder="Найти главу…" aria-label="Поиск по справке">
  <nav id="modern-help-toc"></nav>
  <div class="mh-actions"><button id="modern-help-prev" type="button">← Назад</button><button id="modern-help-next" type="button">Далее →</button></div>
</aside>
<!-- MODERN-HELP-NAV-END -->
"""

SCRIPT = r"""
<!-- MODERN-HELP-SCRIPT-START -->
<script id="modern-help-script">
(function(){
  function ready(fn){if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',fn);else fn();}
  ready(function(){
    var main=document.querySelector('body>main');if(!main)return;
    var requestedHash=window.location.hash;
    if(requestedHash){try{history.replaceState(null,'',window.location.pathname+window.location.search);}catch(ignore){}window.scrollTo(0,0);}
    var state=document.getElementById('modern-help-state');
    var toc=document.getElementById('modern-help-toc');
    var search=document.getElementById('modern-help-search');
    var originalHeadings=[].slice.call(main.querySelectorAll('h2,h3'));var topic=0;
    for(var h=0;h<originalHeadings.length;h++)if(!originalHeadings[h].id)originalHeadings[h].id='help-topic-'+(++topic);

    function splitTable(table){
      var body=table.tBodies&&table.tBodies.length?table.tBodies[0]:null;
      if(!body||body.rows.length<=18)return[table];
      var rows=[].slice.call(body.rows),parts=[];
      for(var start=0;start<rows.length;start+=18){
        var copy=table.cloneNode(false);if(start>0)copy.removeAttribute('id');
        if(table.caption)copy.appendChild(table.caption.cloneNode(true));
        if(table.tHead)copy.appendChild(table.tHead.cloneNode(true));
        var newBody=document.createElement('tbody');
        for(var r=start;r<Math.min(start+18,rows.length);r++)newBody.appendChild(rows[r]);
        copy.appendChild(newBody);parts.push(copy);
      }
      return parts;
    }
    function addAtomic(node,atoms){
      if(node.nodeType===3){if(node.textContent.trim())atoms.push(node);return;}
      if(node.nodeType!==1)return;
      var tag=node.tagName.toLowerCase();
      if(tag==='script'||tag==='style')return;
      if(tag==='table'){var tables=splitTable(node);for(var t=0;t<tables.length;t++)atoms.push(tables[t]);return;}
      if(node.classList.contains('table-wrap')&&node.children.length===1&&node.firstElementChild.tagName==='TABLE'){
        var wrapped=splitTable(node.firstElementChild);for(var w=0;w<wrapped.length;w++){var shell=node.cloneNode(false);shell.appendChild(wrapped[w]);atoms.push(shell);}return;
      }
      if(tag==='details'){
        var summary=node.querySelector(':scope>summary');if(summary){var sh=document.createElement('h2');sh.textContent=summary.textContent;atoms.push(sh);}
        var detailChildren=[].slice.call(node.children);for(var d=0;d<detailChildren.length;d++)if(detailChildren[d]!==summary)addAtomic(detailChildren[d],atoms);return;
      }
      if(tag==='section'){
        var complex=node.querySelectorAll('h2,h3').length>3||node.querySelectorAll('table tr').length>18||node.textContent.length>7500;
        if(!complex){atoms.push(node);return;}
        if(node.id){var anchor=document.createElement('span');anchor.id=node.id;anchor.className='mh-anchor';atoms.push(anchor);node.removeAttribute('id');}
        var sectionChildren=[].slice.call(node.childNodes);for(var s=0;s<sectionChildren.length;s++)addAtomic(sectionChildren[s],atoms);return;
      }
      if(node.classList.contains('neural-reference')){var neuralChildren=[].slice.call(node.childNodes);for(var n=0;n<neuralChildren.length;n++)addAtomic(neuralChildren[n],atoms);return;}
      atoms.push(node);
    }
    var atoms=[],roots=[].slice.call(main.childNodes);
    for(var i=0;i<roots.length;i++){
      var root=roots[i];
      if(root.nodeType===1&&root.tagName==='HEADER')atoms.push(root);else addAtomic(root,atoms);
    }
    while(main.firstChild)main.removeChild(main.firstChild);
    var pages=[];
    function newPage(){var page=document.createElement('article');page.className='mh-page';main.appendChild(page);pages.push(page);return page;}
    var page=newPage();
    for(var aIndex=0;aIndex<atoms.length;aIndex++){
      var atom=atoms[aIndex];page.appendChild(atom);
      if(page.scrollHeight>page.clientHeight&&page.children.length>1){page.removeChild(atom);page=newPage();page.appendChild(atom);}
      if(page.scrollHeight>page.clientHeight&&page.children.length===1){page.classList.add('mh-oversize');page=newPage();}
    }
    if(pages.length>1&&pages[pages.length-1].children.length===0){main.removeChild(pages.pop());}
    function titleOf(node){var heading=node.querySelector('h1,h2,h3');return heading?heading.textContent.trim():'Продолжение';}
    for(var p=0;p<pages.length;p++){
      var footer=document.createElement('div');footer.className='mh-footer';footer.innerHTML='<span>'+titleOf(pages[p])+'</span><span>Страница '+(p+1)+' из '+pages.length+'</span>';pages[p].appendChild(footer);
    }
    for(var oh=0;oh<originalHeadings.length;oh++){
      var heading=originalHeadings[oh];
      var link=document.createElement('a');link.href='#'+heading.id;link.textContent=heading.textContent.trim();if(heading.tagName==='H3')link.className='mh-sub';toc.appendChild(link);
    }
    var links=[].slice.call(toc.querySelectorAll('a'));var current=0;
    function pageIndexFor(element){for(var n=0;n<pages.length;n++){if(pages[n]===element||pages[n].contains(element))return n;}return 0;}
    function activate(index){current=Math.max(0,Math.min(pages.length-1,index));state.textContent='Страница '+(current+1)+' из '+pages.length;for(var n=0;n<links.length;n++){var target=document.getElementById(links[n].hash.slice(1));links[n].classList.toggle('active',target&&pageIndexFor(target)===current);}}
    function nearest(){var best=0,dist=Infinity;for(var n=0;n<pages.length;n++){var d=Math.abs(pages[n].getBoundingClientRect().top-18);if(d<dist){dist=d;best=n;}}activate(best);}
    function go(delta){var index=Math.max(0,Math.min(pages.length-1,current+delta));pages[index].scrollIntoView({behavior:'smooth',block:'start'});activate(index);}
    document.getElementById('modern-help-prev').onclick=function(){go(-1)};document.getElementById('modern-help-next').onclick=function(){go(1)};
    for(var l=0;l<links.length;l++)links[l].onclick=function(){var target=document.getElementById(this.hash.slice(1));if(target)activate(pageIndexFor(target));};
    search.oninput=function(){var q=this.value.toLowerCase().trim();for(var n=0;n<links.length;n++)links[n].classList.toggle('mh-hidden',q&&links[n].textContent.toLowerCase().indexOf(q)<0);};
    search.onkeydown=function(e){if(e.key!=='Enter')return;var visible=toc.querySelector('a:not(.mh-hidden)');if(visible)visible.click();};
    window.addEventListener('scroll',nearest,{passive:true});window.addEventListener('resize',nearest);
    if(requestedHash){setTimeout(function(){var target=document.getElementById(requestedHash.slice(1));if(target){target.scrollIntoView({block:'start'});activate(pageIndexFor(target));}else nearest();},60);}else nearest();
  });
})();
</script>
<!-- MODERN-HELP-SCRIPT-END -->
"""


def strip_block(text: str, start: str, end: str) -> str:
    while start in text and end in text:
        left = text.index(start)
        right = text.index(end, left) + len(end)
        text = text[:left].rstrip() + "\n" + text[right:].lstrip()
    return text


html = HELP.read_text(encoding="utf-8")
for start, end in [
    ("<!-- MODERN-HELP-STYLE-START -->", "<!-- MODERN-HELP-STYLE-END -->"),
    ("<!-- MODERN-HELP-NAV-START -->", "<!-- MODERN-HELP-NAV-END -->"),
    ("<!-- MODERN-HELP-SCRIPT-START -->", "<!-- MODERN-HELP-SCRIPT-END -->"),
]:
    html = strip_block(html, start, end)

if "</head>" not in html or "<body>" not in html or "</body>" not in html:
    raise SystemExit("Unexpected BreezeHelp.html structure")

html = html.replace("</head>", STYLE + "\n</head>", 1)
html = html.replace("<body>", "<body>\n" + NAV, 1)
html = html.replace("</body>", SCRIPT + "\n</body>", 1)
HELP.write_text(html, encoding="utf-8", newline="\n")
STANDALONE.write_text(html, encoding="utf-8", newline="\n")
print(f"Restructured {HELP} ({HELP.stat().st_size} bytes)")
