"""Ordinary Linux MiniUI launcher + automatic PWA log recovery, in a private Xvfb.
Never opens a physical camera, display or existing app. --stage must be an isolated distribution.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import time
import urllib.request
from playwright.sync_api import sync_playwright, expect

p = argparse.ArgumentParser()
p.add_argument('--stage', type=Path, required=True)
p.add_argument('--work', type=Path, required=True)
a = p.parse_args()
stage, work = a.stage.resolve(), a.work.resolve()
work.mkdir(parents=True, exist_ok=True)
root = Path(__file__).resolve().parents[1]
assert not stage.is_relative_to(root)
for name in ['ui', 'web', 'lib']:
    shutil.copytree(root / name, stage / name, dirs_exist_ok=True, ignore=shutil.ignore_patterns('MiniUI.App.dll'))
shutil.copy2(root / 'Xfer.sh', stage / 'Xfer.sh')
source = work / 'input'; source.mkdir(exist_ok=True)
(source / 'test.txt').write_text('synthetic fixture')
logs = work / 'logs'
read_fd, write_fd = os.pipe()
xlog = (work / 'xvfb.log').open('w')
xvfb = subprocess.Popen(['Xvfb', '-displayfd', str(write_fd), '-screen', '0', '1280x900x24', '-nolisten', 'tcp'],
                        pass_fds=(write_fd,), stdout=xlog, stderr=xlog)
os.close(write_fd)
process = None
console = (work / 'app-console.log').open('w')

def browser_records():
    return [json.loads(line) for f in logs.glob('browser-*.jsonl') for line in f.read_text().splitlines()]

try:
    with os.fdopen(read_fd) as fd:
        display = ':' + fd.readline().strip()
    assert display != ':'
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
    origin = f'http://127.0.0.1:{port}'
    env = dict(os.environ, DISPLAY=display, Xfer_LOG_DIR=str(logs))
    process = subprocess.Popen(['bash', str(stage / 'Xfer.sh'), '--path', str(source), '--port', str(port),
        '--output', str(work / 'received'), '--state-dir', str(work / 'state'), '--duration-ms', '110000'],
        env=env, cwd=work, stdout=console, stderr=console)
    for _ in range(100):
        try:
            with urllib.request.urlopen(origin + '/api/status', timeout=1) as r:
                status = json.load(r)
            break
        except Exception:
            if process.poll() is not None: raise AssertionError('MiniUI exited before startup')
            time.sleep(.2)
    else: raise AssertionError('MiniUI did not start its local server')
    assert status['processId'] == process.pid
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True, executable_path=shutil.which('chromium'), args=['--no-sandbox'])
        context = browser.new_context(service_workers='block')
        page = context.new_page()
        shipments = []
        page.on('response', lambda response: shipments.append(response.status) if response.url.endswith('/api/diagnostics') else None)
        page.goto(origin)
        expect(page.locator('#opticalFiles .file-row')).to_have_count(1)
        for _ in range(20):
            if any(r['event'] == 'page.start' for r in browser_records()): break
            page.wait_for_timeout(500)
        else: raise AssertionError('ordinary page startup was not automatically collected')
        print('PASS ordinary Xfer.sh -> real MiniUI -> local server -> automatic browser JSONL on PC, no diagnostic console command', flush=True)

        # Exercise a real worker WASM initialization failure without touching a camera.
        context.route('**/*.wasm', lambda route: route.fulfill(status=503, body='PRIVATE_WASM_RESPONSE'))
        page.evaluate("() => {const c=document.createElement('canvas');c.width=640;c.height=480;const x=c.getContext('2d');window.paint=setInterval(()=>{x.fillStyle='white';x.fillRect(0,0,640,480)},50);navigator.mediaDevices.getUserMedia=async()=>c.captureStream(20);}")
        page.click('#opticalDirection [data-view="receive"]')
        page.click('#readLocalCamera')
        expect(page.locator('#cameraLabel')).to_contain_text('始められません', timeout=20000)
        for _ in range(45):
            failures = [r for r in browser_records() if r['event'] == 'worker.failed']
            if failures: break
            page.wait_for_timeout(1000)
        else: raise AssertionError('worker failure was not collected')
        detail = failures[0]['data']['failure']
        assert detail['stage'] == 'worker.wasm_initialize' and detail['wasmStatus'] == 503, detail
        assert detail['type'] in ['RuntimeError', 'Error'] and detail['site'] == 'qr-worker', detail
        print('PASS actual WASM HTTP 503 identified by worker stage, exception type, HTTP status and source site', flush=True)

        # Network cutoff is browser-only; restoration recovers the saved backlog without console export.
        context.set_offline(True); page.wait_for_timeout(6200)
        before = len(browser_records())
        page.wait_for_timeout(5200)
        assert len(browser_records()) == before
        context.set_offline(False)
        for _ in range(35):
            if any(r['event'] == 'page.offline' for r in browser_records()): break
            page.wait_for_timeout(1000)
        else: raise AssertionError('offline logs were not recovered')
        print('PASS offline history recovered automatically on the existing connection', flush=True)

        # Cross-origin, body size and free-text handling on the real collection endpoint.
        def post(body, headers):
            request = urllib.request.Request(origin + '/api/diagnostics', data=body, headers=headers)
            try:
                with urllib.request.urlopen(request) as r: return r.status
            except urllib.error.HTTPError as error: return error.code
        headers = {'Content-Type': 'application/json', 'X-Xfer-Diagnostics': '1', 'Origin': origin}
        assert post(b'{}', dict(headers, Origin='https://invalid.example')) == 403
        assert post(b'x' * 65537, headers) == 400
        forged = dict(browser_records()[0]); forged['data'] = {'type': 'PRIVATE_ERROR_TEXT', 'file': 'PRIVATE_FILE', 'site': 'PRIVATE_PATH'}
        assert post(json.dumps({'records': [forged]}).encode(), headers) == 200
        text = '\n'.join(f.read_text() for f in logs.glob('*.jsonl'))
        assert all(secret not in text for secret in ['PRIVATE_ERROR_TEXT', 'PRIVATE_FILE', 'PRIVATE_PATH', 'PRIVATE_WASM_RESPONSE'])
        assert shipments and all(status == 200 for status in shipments), shipments
        denied = browser.new_context(service_workers='block')
        denied.add_init_script("Object.defineProperty(window,'indexedDB',{get(){throw new DOMException('denied','SecurityError')}})")
        denied_page = denied.new_page()
        old_runs = {r['run'] for r in browser_records()}
        denied_page.goto(origin)
        for _ in range(20):
            if any(r['event'] == 'page.start' and r['run'] not in old_runs for r in browser_records()): break
            denied_page.wait_for_timeout(500)
        else: raise AssertionError('memory fallback was not collected automatically')
        denied.close()
        print('PASS automatic collection also works when IndexedDB is denied', flush=True)
        (work / 'collection-summary.json').write_text(json.dumps({'shipments': shipments, 'workerFailure': detail,
            'appPid': process.pid, 'xvfbPid': xvfb.pid, 'display': display, 'browserRecords': len(browser_records())}, indent=2))
        print('PASS same-origin boundary, 64 KiB limit and free-text redaction', flush=True)
        browser.close()
finally:
    for child in [process, xvfb]:
        if child and child.poll() is None:
            child.terminate()
            try: child.wait(timeout=8)
            except subprocess.TimeoutExpired: child.kill(); child.wait()
    console.close(); xlog.close()
host = [json.loads(line) for f in logs.glob('trace-*.jsonl') for line in f.read_text().splitlines()]
assert any(r['event'] == 'app.start' and r['correlation'] == 'native' for r in host)
assert any(r['event'] == 'host.exit' for r in host)
print('PASS native startup and exit JSONL, private display and own PID cleanup', flush=True)
