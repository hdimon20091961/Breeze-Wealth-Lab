"""Structural/content checks for the new Help sections, without launching a browser."""
from pathlib import Path
from html.parser import HTMLParser
from urllib.parse import urlparse,unquote
import re,json,hashlib
from build_model_comparison_help import ROOT,PACKAGE,START,END,NAMES,build

class Check(HTMLParser):
    def __init__(self):
        super().__init__();self.ids=[];self.hrefs=[];self.stack=[];self.tables=[];self.table=None;self.row=None;self.text=[]
    def handle_starttag(self,tag,attrs):
        a=dict(attrs)
        if 'id' in a:self.ids.append(a['id'])
        if tag=='a':self.hrefs.append(a['href'])
        if tag not in ['meta','br','hr','img','link','input']:self.stack.append(tag)
        if tag=='table':self.table=[]
        if tag=='tr':self.row=0
        if tag in ['td','th']:self.row+=1
    def handle_endtag(self,tag):
        assert self.stack and self.stack[-1]==tag,(tag,self.stack[-3:])
        self.stack.pop()
        if tag=='tr':self.table.append(self.row)
        if tag=='table':
            assert len(self.table)>1 and len(set(self.table))==1,self.table
            self.tables.append(self.table)
    def handle_data(self,data):self.text.append(data)

paths=[ROOT/'Breeze/BreezeHelp.html',ROOT/'Breeze/BreezeHelp.standalone.html']
before={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}
build()
assert before=={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},'Builder is not idempotent'
summary=[]
for p in paths:
    source=p.read_text(encoding='utf-8');assert source.count(START)==source.count(END)==1
    block=source.split(START,1)[1].split(END,1)[0]
    check=Check();check.feed(block);assert not check.stack
    assert len(check.ids)==len(set(check.ids))
    assert all(x[1:] in check.ids for x in check.hrefs if x.startswith('#'))
    missing=[]
    for href in check.hrefs:
        url=urlparse(href)
        if url.scheme=='file':
            local=unquote(url.path).lstrip('/')
            if not Path(local).exists():missing.append(local)
    assert not missing,missing
    text=' '.join(check.text)
    # Narrative codes must follow their descriptive name in parentheses.
    codes=list(re.finditer(r'(?<![A-Za-z0-9_])('+'|'.join(NAMES)+r')(?![A-Za-z0-9_])',text))
    bare=[m[0] for m in codes if text[m.start()-1:m.start()]!='(']
    assert not bare,bare[:20]
    assert all(s in text for s in ['801.57','752.46','700.34','Decibel','Hurst','CMF','девяти факторов'])
    summary.append(dict(file=str(p.relative_to(ROOT)),tables=len(check.tables),anchors=len(check.ids),references=len(check.hrefs),named_factor_mentions=len(codes),bytes=p.stat().st_size))
result=dict(passed=True,idempotent=True,visual_review='Unavailable: browser policy blocks file protocol. Structural checks only.',files=summary)
(PACKAGE/'help_validation.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=True))
