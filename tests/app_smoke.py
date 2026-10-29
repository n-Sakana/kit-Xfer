"""Exercise Xfer's real HTTP server and browser without a physical camera."""
import json, os, shutil, socket, subprocess, time, urllib.request, urllib.error
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
ROOT=Path(__file__).resolve().parents[1]
ARTIFACTS=ROOT/'artifacts/app-smoke';ARTIFACTS.mkdir(parents=True,exist_ok=True)
source=ARTIFACTS/'input';source.mkdir(exist_ok=True);(source/'note.txt').write_text('Xfer optical smoke\n'*100)
DOTNET=os.environ.get('DOTNET_BIN','dotnet')
with socket.socket() as reserved:
    reserved.bind(('127.0.0.1',0));port=reserved.getsockname()[1]
origin=f'http://127.0.0.1:{port}'
log=(ARTIFACTS/'server.log').open('w')
process=subprocess.Popen([DOTNET,str(ROOT/'bin/Debug/net9.0/Xfer.dll'),'--no-browser','--port',str(port),'--path',str(source)],cwd=ROOT,stdout=log,stderr=log)
def api(path,body=None,expected=200):
    req=urllib.request.Request(origin+path,data=json.dumps(body).encode() if body is not None else None,headers={'Content-Type':'application/json'})
    try:
        with urllib.request.urlopen(req,timeout=15) as r:
            assert r.status==expected
            return json.load(r)
    except urllib.error.HTTPError as error:assert error.code==expected,(path,error.code,error.read())
try:
    for _ in range(100):
        try:status=api('/api/status');break
        except (ConnectionError,urllib.error.URLError):time.sleep(.1)
    else:raise AssertionError('server did not start')
    assert status['application']=='Xfer' and status['initialMode']=='optical'
    for path in ['/api/markdown','/api/vba/inspect','/api/vba/extract']:api(path,{},404)
    for mode in ['markdown','vba']:
        api('/api/folder?mode='+mode,expected=400)
        api('/api/optical/start',dict(format=mode,files=['note.txt'],frameBytes=1000,framesPerSecond=30),400)
    print('PASS application identity and conversion API/mode/transfer removal')
    with sync_playwright() as pw:
        browser=pw.chromium.launch(headless=True,executable_path=os.environ.get('PLAYWRIGHT_CHROMIUM_EXECUTABLE') or shutil.which('google-chrome') or shutil.which('chromium'),args=['--no-sandbox'])
        page=browser.new_page(service_workers='block');errors=[];page.on('pageerror',lambda error:errors.append(str(error)))
        page.goto(origin);expect(page.locator('#opticalFiles .file-row')).to_have_count(1)
        assert page.title()=='Xfer' and page.locator('[data-page-panel]').count()==1
        assert page.locator('[data-transfer-format]').count()==0  # the selected files are sent as they are
        with page.expect_response('**/api/optical/start') as reply:page.click('#showQr')
        transfer=reply.value.json();assert transfer['token']
        expect(page.locator('#qrOverlay')).to_be_visible();expect(page.locator('#qrProgress')).to_contain_text('表示')
        page.click('#closeQr');expect(page.locator('#qrOverlay')).to_be_hidden()
        assert not errors,errors
        print('PASS browser starts, renders and stops real optical QR frames; no script errors')
        browser.close()
    # A selection larger than the ordinary 64 KiB request limit must still start.
    names = ['selection_' + str(i).zfill(5) + '_' + 'x' * 40 + '.txt' for i in range(2001)]
    try:
        for name in names: (source / name).write_text('')
        body = dict(format='original', files=names, frameBytes=1000, framesPerSecond=30, errorCorrection='L')
        assert len(json.dumps(body).encode()) > 65536
        transfer = api('/api/optical/start', body)
        assert transfer['fileCount'] == 2001
        api('/api/optical/stop', dict(token=transfer['token']))
        print('PASS 2001 files and selection JSON over 64 KiB start and stop through real HTTP')
    finally:
        for name in names: (source / name).unlink(missing_ok=True)
    # Verify application isolation even when another app has the requested port.
    from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
    import threading
    class Foreign(BaseHTTPRequestHandler):
        def do_GET(self):
            body=json.dumps(dict(status,application='File2MD',processId=os.getpid())).encode();self.send_response(200);self.end_headers();self.wfile.write(body)
        def log_message(self,*args):pass
    foreign=ThreadingHTTPServer(('127.0.0.1',0),Foreign);threading.Thread(target=foreign.serve_forever,daemon=True).start()
    try:
        refused=subprocess.run([DOTNET,str(ROOT/'bin/Debug/net9.0/Xfer.dll'),'--no-browser','--port',str(foreign.server_port)],cwd=ROOT,capture_output=True,timeout=15)
        assert refused.returncode!=0
        print('PASS occupied port does not replace the other application')
    finally:foreign.shutdown();foreign.server_close()
finally:
    process.terminate()
    try:process.wait(timeout=5)
    except subprocess.TimeoutExpired:process.kill();process.wait()
    log.close()
