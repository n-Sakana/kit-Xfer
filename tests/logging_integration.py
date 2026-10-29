"""Isolated real HTTP + WASM + canvas camera diagnostics test. No physical camera/UI.

Pass an isolated published HTTP app directory and a test work directory. Uses only
its own ephemeral-port process and headless browser. Saves JSONL evidence locally.
"""
import argparse
import base64
import json
import os
from pathlib import Path
import random
import shutil
import socket
import subprocess
import time
import urllib.error
import urllib.request

from playwright.sync_api import sync_playwright, expect

p = argparse.ArgumentParser()
p.add_argument('--app', type=Path, required=True)
p.add_argument('--work', type=Path, required=True)
args = p.parse_args()
work, app = args.work.resolve(), args.app.resolve()
work.mkdir(parents=True, exist_ok=True)
source = work / 'input'
source.mkdir(exist_ok=True)
secret = 'PRIVATE_TRANSFER_FILENAME_67d3.bin'
original = b'PRIVATE_TRANSFER_CONTENT_67d3' + random.Random(10).randbytes(18000)
(source / secret).write_bytes(original)
with socket.socket() as s:
    s.bind(('127.0.0.1', 0))
    port = s.getsockname()[1]
origin = f'http://127.0.0.1:{port}'
server_log = (work / 'server-console.log').open('w')
env = dict(os.environ, Xfer_LOG_DIR=str(work / 'host-logs'))
process = subprocess.Popen([shutil.which('dotnet'), str(app / 'Xfer.dll'), '--no-browser', '--port', str(port), '--path', str(source)],
                           cwd=work, env=env, stdout=server_log, stderr=server_log)


def api(path, body=None, binary=False, headers=None):
    req = urllib.request.Request(origin + path, data=json.dumps(body).encode() if body is not None else None,
                                 headers=headers or {'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=20) as r:
        return r.read() if binary else json.load(r)


try:
    for _ in range(100):
        try:
            status = api('/api/status')
            break
        except (ConnectionError, urllib.error.URLError):
            time.sleep(.1)
    else:
        raise AssertionError('isolated server did not start')
    assert status['processId'] == process.pid
    session = api('/api/optical/start', dict(format='original', files=[secret], frameBytes=1000, framesPerSecond=30, errorCorrection='L'))
    batches = [base64.b64encode(api(f'/api/optical/frames?token={session["token"]}&seq={seq}&count=8', binary=True)).decode()
               for seq in range(0, 160, 8)]
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True, executable_path=shutil.which('chromium'), args=['--no-sandbox'])
        context = browser.new_context(service_workers='block')
        page = context.new_page()
        errors = []
        page.on('pageerror', lambda e: errors.append(str(e)))
        page.goto(origin)
        expect(page.locator('#opticalFiles .file-row')).to_have_count(1)
        # Exercise the actual page's sender, frame API, display and stop path.
        page.click('#showQr')
        expect(page.locator('#qrProgress')).to_contain_text('Display')
        page.wait_for_timeout(5500)
        page.click('#closeQr')
        page.evaluate('''batches => {
          const images = [];
          batches.forEach(b64 => {
            const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
            const view = new DataView(bytes.buffer), side = view.getUint16(4, true);
            const seq = view.getUint32(8, true), count = view.getUint16(6, true);
            XferOptical.unpackFrames(bytes.buffer, side, seq, count).forEach(f => {
              const c = document.createElement('canvas'); c.width = c.height = side;
              c.getContext('2d').putImageData(new ImageData(f.pixels, side, side), 0, 0); images.push(c);
            });
          });
          const canvas = document.createElement('canvas'); canvas.width = 1280; canvas.height = 960;
          const ctx = canvas.getContext('2d'); let frame = -12;
          window.syntheticTimer = setInterval(() => {
            const qr = images[Math.max(0, frame++) % images.length];
            ctx.fillStyle = 'white'; ctx.fillRect(0,0,1280,960); ctx.imageSmoothingEnabled = false;
            const size = qr.width * 5; ctx.drawImage(qr, (1280-size)/2, (960-size)/2, size, size);
          }, 100);
          navigator.mediaDevices.getUserMedia = async () => canvas.captureStream(30);
        }''', batches)
        page.click('#opticalDirection [data-view="receive"]')
        page.evaluate("document.getElementById('receiveOpenFolder').setAttribute('aria-checked','false')")
        page.click('#readLocalCamera')
        expect(page.locator('#receiveState')).to_have_text('Received', timeout=45000)
        received = Path(page.locator('#receiveOutputPath').inner_text()) / secret
        assert received.read_bytes() == original
        success_log = page.evaluate('XferDiagnostics.exportJsonl()')
        (work / 'browser-success.jsonl').write_text(success_log)
        print('PASS real HTTP sender -> synthetic canvas camera -> actual WASM -> HTTP relay -> C# verify/save; bytes identical', flush=True)

        # Keep a real receive request pending after successful camera startup.
        held = []
        page.route('**/api/optical/receive/frames', lambda route: held.append(route))
        page.click('#readLocalCamera')
        page.wait_for_timeout(6500)
        pending_log = page.evaluate('XferDiagnostics.exportJsonl()')
        assert any(r['event'] == 'page.heartbeat' and any(w['stage'] == 'http.body' and w['ageMs'] >= 1000 for w in r['data']['waiting'])
                   for r in map(json.loads, pending_log.splitlines()))
        context.set_offline(True)
        for route in held:
            try:
                route.abort('internetdisconnected')
            except Exception:
                pass
        page.unroute('**/api/optical/receive/frames')
        page.wait_for_timeout(6000)
        offline_log = page.evaluate('XferDiagnostics.exportJsonl()')
        (work / 'browser-offline.jsonl').write_text(offline_log)
        records = [json.loads(line) for line in offline_log.splitlines()]
        names = {r['event'] for r in records}
        assert {'relay.retry', 'camera.status', 'page.heartbeat', 'http.body.failed'} <= names
        assert page.evaluate("new Promise(r=>{const q=indexedDB.open('Xfer-diagnostics');q.onsuccess=()=>{const c=q.result.transaction('events').objectStore('events').count();c.onsuccess=()=>r(c.result);};})") > 0
        page.click('#readLocalCamera')
        page.wait_for_timeout(100)
        print('PASS pending HTTP age, offline relay retry and camera counters persist locally without PC connectivity', flush=True)
        context.set_offline(False)
        page.evaluate('clearInterval(window.syntheticTimer)')
        page.evaluate("() => {navigator.mediaDevices.getUserMedia=async()=>{throw new DOMException('PRIVATE_DENIAL_MESSAGE','NotAllowedError')};}")
        page.click('#readLocalCamera')
        expect(page.locator('#cameraLabel')).to_contain_text('Camera access is denied')
        all_log = page.evaluate('XferDiagnostics.exportJsonl()')
        (work / 'browser-events.jsonl').write_text(all_log)
        for forbidden in [secret, session['token'], 'PRIVATE_TRANSFER_CONTENT_67d3', 'PRIVATE_DENIAL_MESSAGE', batches[0]]:
            assert forbidden not in all_log
        assert 'camera.open.failed' in all_log and 'NotAllowedError' in all_log
        assert not errors, errors
        print('PASS camera failure reason, cancellation, sensitive-data exclusion and no page script errors', flush=True)

        # Ordinary background collection: no call to the diagnostic upload API from test code.
        for _ in range(40):
            collected = '\n'.join(f.read_text() for f in (work / 'host-logs').glob('browser-*.jsonl'))
            if 'NotAllowedError' in collected and 'page.offline' in collected and 'receive.complete' in collected:
                break
            page.wait_for_timeout(1000)
        else:
            raise AssertionError('PWA logs were not automatically collected after reconnection')
        for forbidden in [secret, session['token'], 'PRIVATE_TRANSFER_CONTENT_67d3', 'PRIVATE_DENIAL_MESSAGE']:
            assert forbidden not in collected
        print('PASS automatic PC collection of completion, offline interval and camera failure after reconnection', flush=True)

        # Test one origin-wide ring across reloads, not an unbounded record set per run.
        page.evaluate("async()=>{for(let n=0;n<166;n++){for(let i=0;i<100;i++)XferDiagnostics.event('page.visibility','',{visible:true});await XferDiagnostics.flush();}}")
        before = page.evaluate('XferDiagnostics.exportJsonl()')
        assert len(before.splitlines()) <= 16384 and 'NotAllowedError' not in before
        page.reload()
        after = page.evaluate('XferDiagnostics.exportJsonl()')
        assert 'page.visibility' in after and len(after.splitlines()) <= 16384
        archived = page.evaluate("new Promise(r=>{const q=indexedDB.open('Xfer-diagnostics');q.onsuccess=()=>{const c=q.result.transaction('incidents').objectStore('incidents').getAll();c.onsuccess=()=>r(c.result);};})")
        assert len(archived) <= 4096 and any(r['event'] == 'camera.open.failed' for r in archived)
        # Storage-denied mode must not break ordinary UI or grow memory indefinitely.
        denied = browser.new_context(service_workers='block')
        denied.add_init_script("Object.defineProperty(window,'indexedDB',{get(){throw new DOMException('denied','SecurityError')}})")
        denied_page = denied.new_page(); denied_page.goto(origin)
        expect(denied_page.locator('#opticalFiles .file-row')).to_have_count(1)
        fallback = denied_page.evaluate("async()=>{for(let i=0;i<500;i++)XferDiagnostics.event('test.memory');return await XferDiagnostics.exportJsonl();}")
        assert len(fallback.splitlines()) <= 128 and 'logging.loss' in fallback
        print('PASS IndexedDB 16384-record ring, protected failure window after ring overwrite/reload, 128-record memory fallback', flush=True)
        denied.close(); browser.close()
    api('/api/optical/stop', {'token': session['token']})
    time.sleep(5.5)
finally:
    if process.poll() is None:
        try:
            api('/api/shutdown', {}, headers={'X-Xfer-Process-Id': str(process.pid)})
            process.wait(timeout=5)
        except Exception:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait()
    server_log.close()

host_text = '\n'.join(path.read_text() for path in (work / 'host-logs').glob('trace-*.jsonl'))
for forbidden in [secret, session['token'], 'PRIVATE_TRANSFER_CONTENT_67d3']:
    assert forbidden not in host_text
host = [json.loads(line) for line in host_text.splitlines() if line]
names = {r['event'] for r in host}
assert {'send.ready', 'receive.complete', 'receive.save.ok', 'receive.stopped', 'http.begin', 'http.end'} <= names
assert any(r['event'] == 'receive.complete' and r['correlation'] == session['transferId'] for r in host)
print('PASS host JSONL, transfer correlation, privacy and own-process shutdown', flush=True)
