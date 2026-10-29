#!/usr/bin/env python3
"""Real Chromium/WASM tests with synthetic camera images and explicitly mocked HTTP APIs.
No physical camera, WPF, or modified C# execution is claimed by this test.
"""
from __future__ import annotations
import base64
import json
import os
from pathlib import Path
import random
import re
from types import SimpleNamespace
import shutil
import struct
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit, parse_qs

import cv2
import numpy as np
from PIL import Image, ImageFilter
import qrcode
from qrcode.util import QRData, MODE_8BIT_BYTE
from playwright.sync_api import sync_playwright

OFFLINE = '--in-memory' in __import__('sys').argv
ROOT = Path(__file__).resolve().parents[1]
GENERATED = ROOT / 'tests' / 'generated'
RESULTS = ROOT / 'tests' / 'results'
GENERATED.mkdir(exist_ok=True)
RESULTS.mkdir(exist_ok=True)


def transfer_frame(seq: int = 0, size: int = 1000) -> bytes:
    length = size - 20
    total = 100_000
    return struct.pack('<2sHIHHII', b'\xd1\x0c', 1776, seq, (total+length-1)//length,
                       length, total, 0x12345678) + random.Random(2000+seq).randbytes(length)


def qr_image(data: bytes) -> tuple[Image.Image, list[list[bool]], int]:
    qr = qrcode.QRCode(error_correction=qrcode.constants.ERROR_CORRECT_L, box_size=1, border=4, mask_pattern=4)
    qr.add_data(QRData(data, mode=MODE_8BIT_BYTE), optimize=0)
    qr.make(fit=True)
    return qr.make_image(fill_color='black', back_color='white').convert('RGB'), qr.get_matrix(), qr.version


def scene(qr: Image.Image, name: str) -> Image.Image:
    width, height = 1280, 960
    image = Image.new('RGB', (width, height), (190,190,190))
    size = 460
    if name == 'small_300': size = 300
    if name == 'small_250': size = 250
    if name == 'small_200': size = 200
    code = qr.resize((size,size), Image.Resampling.NEAREST)
    if name == 'rotate_15': code = code.rotate(15, Image.Resampling.BICUBIC, expand=True, fillcolor=(190,190,190))
    if name == 'rotate_30': code = code.rotate(30, Image.Resampling.BICUBIC, expand=True, fillcolor=(190,190,190))
    x,y=(width-code.width)//2,(height-code.height)//2
    if name == 'top_left': x,y=5,5
    if name == 'top_right': x,y=width-code.width-5,5
    if name == 'bottom_left': x,y=5,height-code.height-5
    if name == 'bottom_right': x,y=width-code.width-5,height-code.height-5
    if name == 'off_center': x,y=100,340
    if name == 'perspective':
        src=np.float32([[0,0],[size-1,0],[size-1,size-1],[0,size-1]])
        dst=np.float32([[x+35,y+45],[x+size-25,y],[x+size+15,y+size-20],[x,y+size]])
        return Image.fromarray(cv2.warpPerspective(np.array(code),cv2.getPerspectiveTransform(src,dst),(width,height),borderValue=(190,190,190)))
    if name == 'multiple_qr':
        x,y=800,470
        pairing,_,_=qr_image(b'https://Xfer.example.test:10000/')
        pairing=pairing.resize((310,310),Image.Resampling.NEAREST)
        image.paste(pairing,(20,20))
    image.paste(code,(x,y))
    if name == 'blur_08': image=image.filter(ImageFilter.GaussianBlur(0.8))
    if name == 'blur_12': image=image.filter(ImageFilter.GaussianBlur(1.2))
    return image


CASES=['center','top_left','top_right','bottom_left','bottom_right','off_center',
       'rotate_15','rotate_30','perspective','blur_08','blur_12','small_300','small_250','small_200','multiple_qr']
frames={}
matrices={}
versions={}
for variant,size in [('balanced',1000),('original',2953)]:
    data=transfer_frame(0,size)
    code,matrix,version=qr_image(data)
    frames[variant]=base64.b64encode(data).decode()
    matrices[variant]=matrix
    versions[variant]=version
    for name in CASES: scene(code,name).save(GENERATED/f'{variant}_{name}.png')
# Short animated camera stream for the actual app UI -> WASM -> upload pipeline.
for seq in range(12):
    code,_,_=qr_image(transfer_frame(seq))
    scene(code,'off_center').save(GENERATED/f'video_{seq}.png')
Image.new('RGB',(1280,960),(190,190,190)).save(GENERATED/'blank.png')
link,_,_=qr_image(b'https://example.test/no-transfer')
scene(link,'center').save(GENERATED/'foreign.png')


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def send_content(self, data: bytes, content_type: str='application/json', status: int=200):
        self.send_response(status)
        self.send_header('Content-Type',content_type)
        self.send_header('Content-Length',str(len(data)))
        self.send_header('Cache-Control','no-store')
        self.end_headers()
        try: self.wfile.write(data)
        except (BrokenPipeError,ConnectionResetError): pass
    def do_GET(self):
        path=urlsplit(self.path).path
        if path=='/worker-test.html':
            self.send_content(b'<!doctype html><script src="/optical-core.js"></script>', 'text/html');return
        if path=='/original-worker.js': p=ROOT/'tests/fixtures/qr-worker-original.js'
        elif path.startswith('/generated/'): p=GENERATED/Path(path).name
        else: p=ROOT/'web'/('index.html' if path=='/' else path.lstrip('/'))
        if not p.is_file(): self.send_content(b'{"error":"not found"}',status=404);return
        types={'.html':'text/html','.js':'text/javascript','.css':'text/css','.wasm':'application/wasm','.png':'image/png','.svg':'image/svg+xml','.webmanifest':'application/manifest+json'}
        self.send_content(p.read_bytes().replace(b'__Xfer_BUILD_ID__',b'regression') if p.suffix in ['.html','.js','.css'] else p.read_bytes(),types.get(p.suffix,'application/octet-stream'))


server=None
origin='http://memory.test'
if not OFFLINE:
    server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
    threading.Thread(target=server.serve_forever,daemon=True).start()
    origin=f'http://127.0.0.1:{server.server_port}'
worker_helpers=r"""
window.testWorkers = {};
window.makeWorker = async (name,url) => new Promise((resolve,reject)=>{
  const worker=new Worker(url);const timer=setTimeout(()=>reject(new Error('worker init timeout')),12000);
  worker.onerror=e=>{clearTimeout(timer);reject(new Error(e.message));};
  worker.onmessage=e=>{if(e.data.id===-1){clearTimeout(timer);testWorkers[name]=worker;resolve(e.data);}};
});
window.decodeImage=async (workerName,url,id,crop,session)=>{
  const image=new Image();image.src=(window.__images && window.__images[url]) || url;await image.decode();
  const canvas=document.createElement('canvas');
  const region=crop || {x:0,y:0,width:image.width,height:image.height};
  canvas.width=region.width;canvas.height=region.height;
  const ctx=canvas.getContext('2d',{willReadFrequently:true});
  ctx.drawImage(image,region.x,region.y,region.width,region.height,0,0,region.width,region.height);
  const rgba=ctx.getImageData(0,0,canvas.width,canvas.height);
  const worker=testWorkers[workerName];
  const start=performance.now();
  return await new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>reject(new Error('decode timeout')),12000);
    worker.onmessage=e=>{if(e.data.id!==id)return;clearTimeout(timer);
      const bytes=e.data.bytes ? new Uint8Array(e.data.bytes):null;
      resolve({ms:performance.now()-start,bytes:bytes?btoa(String.fromCharCode(...bytes)):null,position:e.data.position});};
    worker.postMessage({id,buf:rgba.data.buffer,w:canvas.width,h:canvas.height,session},[rgba.data.buffer]);
  });
};
"""
results={'transport':'in-memory local assets' if OFFLINE else 'localhost HTTP', 'kind':'synthetic browser and mocked API tests; not physical-camera or modified-C# tests', 'cases':[], 'checks':[]}


def passed(name: str):
    results['checks'].append({'name':name,'passed':True})
    print('PASS',name,flush=True)


def packed_matrix(sequence: int, count: int) -> bytes:
    matrix=matrices['balanced'];side=len(matrix);stride=(side*side+7)//8
    bits=bytearray(stride)
    for y,row in enumerate(matrix):
        for x,dark in enumerate(row):
            if dark:
                bit=y*side+x;bits[bit//8]|=0x80>>(bit%8)
    return struct.pack('<4sHHI',b'FQR1',side,count,sequence)+bytes(bits)*count



# The in-memory option performs no browser navigation/network access. It is useful
# when a managed browser forbids URLs. Only the test asset loader changes: QR/app
# logic remains the shipped JavaScript and the shipped WASM bytes.
image_data={f'/generated/{p.name}':'data:image/png;base64,'+base64.b64encode(p.read_bytes()).decode()
            for p in GENERATED.glob('*.png')}
wasm_name='zxing_reader-EOacYbLr.wasm'
wasm_b64=base64.b64encode((ROOT/'web'/wasm_name).read_bytes()).decode()
source_map={f'/{name}':(ROOT/'web'/name).read_text() for name in ['optical-core.js','zxing-reader.js','qr-worker.js']}
source_map['/original-worker.js']=(ROOT/'tests/fixtures/qr-worker-original.js').read_text()
for key in ['/zxing-reader.js','/original-worker.js']:
    # Replace only URL-based resource resolution with an in-memory WASM override.
    source_map[key]=source_map[key].replace('new URL("'+wasm_name+'",self.location.href).href','"memory:reader.wasm"')
    source_map[key]=source_map[key].replace('oe({overrides:{locateFile:', 'oe({overrides:{wasmBinary:self.__wasmBytes,locateFile:')


def prepare_memory_page(page, bad_wasm=False):
    page.set_content('<!doctype html><html><head></head><body></body></html>')
    page.evaluate(r"""({sources,wasm,images,bad})=>{
      window.__images=images;
      const NativeWorker=window.Worker;
      window.Worker=function(url){
        const path=String(url).split('?')[0];
        if(!sources[path])throw new Error('Unexpected test worker: '+url);
        const prefix='self.__wasmBytes=Uint8Array.from(atob('+JSON.stringify(wasm)+'),c=>c.charCodeAt(0));'+
          (bad?'self.__wasmBytes[0]=99;':'')+
          'const sources='+JSON.stringify(sources)+';self.importScripts=(...urls)=>{for(const url of urls){const source=sources[url.split("?")[0]];if(!source)throw new Error("Missing local test asset "+url);(0,eval)(source);}};';
        const blob=new Blob([prefix,sources[path]],{type:'text/javascript'});
        const address=URL.createObjectURL(blob),worker=new NativeWorker(address);
        URL.revokeObjectURL(address);return worker;
      };
      Object.defineProperty(navigator,'mediaDevices',{configurable:true,value:{getUserMedia:async()=>{throw new DOMException('No physical test camera','NotAllowedError');}}});
      navigator.sendBeacon=()=>true;
    }""", {'sources':source_map,'wasm':wasm_b64,'images':image_data,'bad':bad_wasm})
    page.add_script_tag(content=(ROOT/'web/optical-core.js').read_text())


class MemoryRoute:
    def __init__(self, request):
        self.request=SimpleNamespace(url=request['url'],method=request['method'],post_data=request.get('body'))
        self.result=None
    def fulfill(self, status=200, body=None, content_type=None, json=None):
        raw=__import__('json').dumps(json).encode() if json is not None else body or b''
        if isinstance(raw,str):raw=raw.encode()
        self.result={'status':status,'body':base64.b64encode(raw).decode(),
                     'contentType':content_type or ('application/json' if json is not None else 'text/plain')}


def memory_ui(page, api):
    prepare_memory_page(page)
    def send(request):
        route=MemoryRoute(request);api(route)
        assert route.result is not None
        return route.result
    page.expose_function('__apiMock',send)
    page.evaluate(r"""()=>{window.fetch=async(url,options={})=>{
      if(options.signal && options.signal.aborted)throw new DOMException('Aborted','AbortError');
      const r=await window.__apiMock({url:String(url),method:options.method||'GET',body:options.body});
      if(options.signal && options.signal.aborted)throw new DOMException('Aborted','AbortError');
      return new Response(Uint8Array.from(atob(r.body),c=>c.charCodeAt(0)),{status:r.status,headers:{'Content-Type':r.contentType}});
    };}""")
    html=(ROOT/'web/index.html').read_text()
    html=re.sub(r'<script\b[^>]*>.*?</script>','',html,flags=re.S)
    html=re.sub(r'<link\b[^>]*>','',html)
    # DOM replacement retains the in-memory test shims; no resource URLs are loaded.
    page.evaluate('(html)=>{document.open();document.write(html);document.close();}',html)
    page.add_style_tag(content=(ROOT/'web/app.css').read_text())
    page.add_script_tag(content=(ROOT/'web/diagnostics.js').read_text())
    page.add_script_tag(content=(ROOT/'web/app.js').read_text())


with sync_playwright() as pw:
    executable=os.environ.get('PLAYWRIGHT_CHROMIUM_EXECUTABLE') or shutil.which('chromium') or shutil.which('google-chrome')
    browser=pw.chromium.launch(headless=True,executable_path=executable,args=['--no-sandbox'])
    results['browser']=browser.version
    context=browser.new_context(service_workers='block')
    page=context.new_page()
    if OFFLINE: prepare_memory_page(page)
    else: page.goto(origin+'/worker-test.html')
    page.evaluate("()=>{"+worker_helpers+"}")
    assert page.evaluate("makeWorker('new','/qr-worker.js')")['ready']
    page.evaluate("makeWorker('old','/original-worker.js')")
    passed('new worker initializes the bundled WASM')
    # Compare old defaults/worker against new defaults/worker at the same physical QR size.
    for index,name in enumerate(CASES):
        row={'name':name}
        for worker,variant in [('old','original'),('new','balanced')]:
            decoded=page.evaluate('p=>decodeImage(...p)',[worker,f'/generated/{variant}_{name}.png',index+1,None])
            row[worker]={'success':decoded['bytes']==frames[variant],'ms':round(decoded['ms'],3)}
        results['cases'].append(row)
        print('CASE',name,row['old']['success'],row['new']['success'],flush=True)
    for row in results['cases'][:6]: assert row['new']['success'],row
    passed('new default decodes center, all four edges and off-center')
    assert next(row for row in results['cases'] if row['name']=='multiple_qr')['new']['success']
    passed('transfer QR selected in the presence of a pairing/link QR')
    for name in ['blank','foreign']:
        decoded=page.evaluate('p=>decodeImage(...p)',['new',f'/generated/{name}.png',99,None])
        assert decoded['bytes'] is None
    passed('blank and non-transfer QR never enter receive upload')
    matching_session=page.evaluate('b64=>XferOptical.parseFrame(Uint8Array.from(atob(b64),c=>c.charCodeAt(0))).session',frames['balanced'])
    decoded=page.evaluate('p=>decodeImage(...p)',['new','/generated/balanced_center.png',99,None,matching_session])
    assert decoded['bytes']==frames['balanced']
    decoded=page.evaluate('p=>decodeImage(...p)',['new','/generated/balanced_center.png',99,None,'different-session'])
    assert decoded['bytes'] is None
    passed('worker session pin ignores other transfers while accepting the active one')
    # Same data, same image: compare the old full-frame path with new tracked ROI path.
    first=page.evaluate('p=>decodeImage(...p)',['new','/generated/balanced_off_center.png',100,None])
    assert first['bytes']==frames['balanced']
    crop=page.evaluate('position=>{const c=XferOptical;const t=c.trackPosition(position,{x:0,y:0},1280,960,1000);return c.scanRegion(1280,960,t,1,1001);}',first['position'])
    assert not crop['full']
    times={'old_full':[],'new_roi':[]}
    for n in range(18):
        for key,worker,region in [('old_full','old',None),('new_roi','new',crop)]:
            d=page.evaluate('p=>decodeImage(...p)',[worker,'/generated/balanced_off_center.png',200+n,region])
            assert d['bytes']==frames['balanced']
            if n>=3: times[key].append(d['ms'])
    results['roi_benchmark']={'region':crop,'camera_pixels':1280*960,'roi_pixels':crop['width']*crop['height'],
        'samples':15,'median_ms':{k:round(float(np.median(v)),3) for k,v in times.items()}}
    passed('tracked crop returns identical frame bytes (15 timed samples after warm-up)')
    # Corrupt/missing WASM must be distinguished from a camera alignment miss.
    failure_context=browser.new_context(service_workers='block')
    failure_context.route('**/*.wasm',lambda route:route.fulfill(status=404,body='missing'))
    failure_page=failure_context.new_page()
    if OFFLINE: prepare_memory_page(failure_page,True)
    else: failure_page.goto(origin+'/worker-test.html')
    failure_page.evaluate("()=>{"+worker_helpers+"}")
    init=failure_page.evaluate("makeWorker('failed','/qr-worker.js')")
    assert init.get('fatal') and not init.get('ready')
    passed('WASM initialization failure produces a fatal result, not a camera miss')
    failure_context.close()

    # Browser UI smoke and lifecycle tests. These API results are mocks, not C# server results.
    ui=context.new_page();errors=[];ui.on('pageerror',lambda error:errors.append(str(error)))
    state={'actual_frame_bytes':1000,'start':0,'batches':0,'stops':[],'uploads':[],'receive_ids':[], 'fail_uploads':1, 'folder_revision':0,'remote':False,'heartbeat':0}
    folder={'path':'/mock/input','directoryPath':'/mock/input','sourceKind':'folder','files':[
        {'name':'sample.txt','extension':'.txt','size':100000,'bytes':100000,'length':100000}]}
    def api(route):
        request=route.request;path=urlsplit(request.url).path
        body=json.loads(request.post_data or '{}') if request.method=='POST' else {}
        result={}
        if path=='/api/status': result={'device':'Browser regression','role':'remote' if state['remote'] else 'local','initialMode':'optical','capabilities':{}}
        elif path=='/api/folder': result={**folder,'revision':state['folder_revision']}
        elif path=='/api/remotes': result={'remotes':[]}
        elif path=='/api/remote-entry': route.fulfill(status=503,json={'error':'test: Tailnet unavailable'});return
        elif path=='/api/optical/start':
            state['start']+=1
            result={'token':f'mock{state["start"]}','frameBytes':state['actual_frame_bytes'],'framesPerSecond':30,'errorCorrection':'L',
                    'qrVersion':versions['balanced'],'sourceBlocks':103,'originalBytes':100000,'minimumSeconds':3.5,'label':'sample.txt'}
        elif path=='/api/optical/frames':
            state['batches']+=1;q=parse_qs(urlsplit(request.url).query)
            route.fulfill(body=packed_matrix(int(q['seq'][0]),int(q['count'][0])),content_type='application/octet-stream');return
        elif path=='/api/optical/stop': state['stops'].append(body['token']);result={'stopped':True}
        elif path=='/api/optical/receive/start': state['receive_ids'].append(body['receiverId']);result={'started':True}
        elif path=='/api/optical/receive/stop': result={'stopped':True}
        elif path=='/api/optical/receive/frames':
            if state['fail_uploads']:
                state['fail_uploads']-=1;route.fulfill(status=503,json={'error':'synthetic temporary outage'});return
            for b64 in body['frames']:
                data=base64.b64decode(b64);seq=struct.unpack_from('<I',data,4)[0]
                assert data==transfer_frame(seq), 'camera decoded bytes differ from original fixture'
                state['uploads'].append(seq)
            n=len(set(state['uploads']));complete=n>=6
            result={'recognized':True,'complete':complete,'framesCollected':n,'sourceBlocks':12,'solvedBlocks':n,
                    'totalBytes':100000,'progress':1 if complete else n/12,'elapsedSeconds':1,'kilobytesPerSecond':4,
                    'label':'synthetic','fileCount':1,'outputPath':'/mock/output'}
        elif path=='/api/remotes/heartbeat': state['heartbeat']+=1;result={'command':None}
        route.fulfill(json=result)
    if OFFLINE: memory_ui(ui,api)
    else:
        ui.route('**/api/**',api)
        ui.goto(origin+'/')
    ui.wait_for_function("document.getElementById('showQr').disabled===false")
    assert ui.locator('#frameAmount').input_value()=='1000'
    assert ui.locator('#frameRate').input_value()=='30'
    assert ui.title() == 'Xfer'
    assert ui.locator('[data-page-panel]').count() == 1
    passed('initial optical screen and default settings')
    ui.select_option('#frameAmount','2953');ui.select_option('#errorCorrection','H')
    assert int(ui.locator('#frameAmount').input_value())<=1273
    assert ui.locator('#frameAmount option[value="2953"]').is_disabled()
    ui.select_option('#errorCorrection','L')
    passed('EC change disables invalid QR capacities and selects a valid amount')
    ui.click('#showQr');ui.wait_for_function("document.getElementById('qrProgress').textContent.includes('表示')")
    assert not ui.locator('#qrOverlay').is_hidden()
    assert state['batches']>0
    # Resize, including mobile landscape, may not blank or clip the QR.
    for width,height in [(390,844),(844,390),(1280,800)]:
        ui.set_viewport_size({'width':width,'height':height});ui.wait_for_timeout(150)
        check=ui.evaluate("""()=>{const c=document.getElementById('qrFrame');const r=c.getBoundingClientRect();const a=c.getContext('2d').getImageData(0,0,c.width,c.height).data;return {left:r.left,top:r.top,right:r.right,bottom:r.bottom,black:a.some((v,i)=>i%4===0&&v===0),opaque:a[3]===255};}""")
        assert check['opaque'] and check['black']
        assert check['left']>=-1 and check['top']>=-1 and check['right']<=width+1 and check['bottom']<=height+1,check
    passed('packed QR sender renders and remains visible after desktop/portrait/landscape resize')
    ui.click('#closeQr');ui.wait_for_timeout(100);assert ui.locator('#qrOverlay').is_hidden();assert state['stops']
    passed('closing QR stops its server token')
    ui.select_option('#frameAmount','1000');state['actual_frame_bytes']=400
    ui.click('#showQr');ui.wait_for_function("document.getElementById('qrProgress').textContent.includes('表示')")
    assert ui.locator('#frameAmount').input_value()=='1000'
    ui.click('#closeQr');state['actual_frame_bytes']=1000
    passed('a tiny actual frame does not reduce the next transfer payload setting')
    # Virtual camera driven by actual canvas video frames. All decoding uses bundled WASM.
    ui.evaluate(r"""async()=>{
      const images=await Promise.all(Array.from({length:12},(_,i)=>new Promise(async resolve=>{
        const image=new Image();image.src=(window.__images && window.__images['/generated/video_'+i+'.png']) || '/generated/video_'+i+'.png';await image.decode();resolve(image);}))); 
      const canvas=document.createElement('canvas');canvas.width=1280;canvas.height=960;
      const ctx=canvas.getContext('2d');let frame=0;
      window.virtualCameraTimer=setInterval(()=>{ctx.drawImage(images[frame++%images.length],0,0);},100);
      navigator.mediaDevices.getUserMedia=async()=>canvas.captureStream(30);
    }""")
    # The camera button belongs to 受け取る, as on the desktop screen.
    ui.click('#opticalDirection [data-view="receive"]');assert ui.locator('#readLocalCamera').is_visible()
    ui.click('#readLocalCamera');ui.wait_for_function("document.getElementById('receiveState').textContent==='受け取りました'",timeout=20000)
    assert len(set(state['uploads']))>=6 and state['fail_uploads']==0
    assert not errors, errors
    passed('canvas camera -> real WASM -> bounded uploads -> completion survives a 503 response')
    ui.screenshot(path=str(RESULTS/'receive-ui.png'),full_page=True)
    first_id=state['receive_ids'][-1]
    ui.click('#readLocalCamera');ui.wait_for_function("document.getElementById('readLocalCamera').textContent.includes('やめる')")
    ui.click('#readLocalCamera');ui.wait_for_timeout(100)
    assert len(state['receive_ids'])>=2 and state['receive_ids'][-1]!=first_id
    passed('camera restart gets a new receiver ID; stopping camera clears its stream')
    assert ui.evaluate("document.getElementById('cameraVideo').srcObject===null")
    ui.evaluate('clearInterval(window.virtualCameraTimer)')
    # Denied permission is not retried with another permission request.
    ui.evaluate("()=>{window.deniedCalls=0;navigator.mediaDevices.getUserMedia=async()=>{window.deniedCalls++;throw new DOMException('denied','NotAllowedError');};}")
    ui.click('#readLocalCamera');ui.wait_for_function("document.getElementById('cameraLabel').textContent.includes('許可')")
    assert ui.evaluate('window.deniedCalls')==1
    passed('camera denial produces one permission attempt, not an unconditional retry')
    # Remote polling may update the file list but must not close a running QR.
    state['remote']=True
    if OFFLINE:
        ui.close();ui=context.new_page();ui.on('pageerror',lambda error:errors.append(str(error)));memory_ui(ui,api)
    else: ui.reload()
    ui.wait_for_function("document.getElementById('showQr').disabled===false")
    ui.click('#showQr');ui.wait_for_function("document.getElementById('qrProgress').textContent.includes('表示')")
    state['folder_revision']+=1
    ui.wait_for_timeout(2700)
    assert not ui.locator('#qrOverlay').is_hidden()
    passed('remote folder metadata polling does not stop an active QR display')
    ui.click('#closeQr')
    state['remote']=False
    assert not errors,errors
    results['ui_page_errors']=errors
    browser.close()
if server is not None: server.shutdown()
results['success_counts']={k:sum(row[k]['success'] for row in results['cases']) for k in ['old','new']}
(RESULTS/'browser-results.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:results[k] for k in ['browser','success_counts','roi_benchmark']},ensure_ascii=False,indent=2))
