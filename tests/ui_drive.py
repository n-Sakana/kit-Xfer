"""Drive the MiniUI Xfer window through MiniUI's stdio MCP and save screenshots.

Windows:  python tests/ui_drive.py [--out tests/results/screens] [--visible]
Linux:    python3 tests/ui_drive.py --linux --out tests/results/screens/linux   (runs ./Xfer.sh)
Windows uses runtime/windows-x64 (or --runtime DIR) and the page in ui/. Input goes through MiniUI's own
hit-tested click path (ui_click); nothing calls page functions directly.
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class Mcp:
    def __init__(self, command: list[str], log: Path):
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.replies: queue.Queue = queue.Queue()
        self.counter = 0
        self.app = ""
        self.log = log.open("w", encoding="utf-8")
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=self._errors, daemon=True).start()

    def _read(self):
        for line in self.process.stdout:
            try:
                self.replies.put(json.loads(line.decode("utf-8")))
            except ValueError:
                self.log.write("non-json: " + line.decode("utf-8", "replace"))
        self.replies.put(EOFError("MCP output ended"))

    def _errors(self):
        for line in self.process.stderr:
            self.log.write(line.decode("utf-8", "replace"))
            self.log.flush()

    def request(self, method: str, params: dict | None = None, timeout: float = 30) -> dict:
        self.counter += 1
        message = {"jsonrpc": "2.0", "id": self.counter, "method": method}
        if params is not None:
            message["params"] = params
        self.process.stdin.write((json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8"))
        self.process.stdin.flush()
        deadline = time.time() + timeout
        while True:
            reply = self.replies.get(timeout=max(0.01, deadline - time.time()))
            if isinstance(reply, BaseException):
                raise reply
            if reply.get("id") == self.counter:
                if "error" in reply:
                    raise RuntimeError(json.dumps(reply["error"], ensure_ascii=False))
                return reply["result"]

    def tool(self, name: str, arguments: dict) -> dict:
        result = self.request("tools/call", {"name": name, "arguments": arguments})
        if result.get("isError"):
            raise RuntimeError(json.dumps(result.get("structuredContent"), ensure_ascii=False))
        return result

    def start(self):
        self.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "xfer-ui", "version": "1"}})
        self.process.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.process.stdin.flush()
        self.app = self.tool("runtime_capabilities", {})["structuredContent"]["applicationId"]

    def observe(self) -> dict:
        return self.tool("ui_observe", {"applicationId": self.app, "image": False, "limit": 2048})["structuredContent"]

    def element(self, element_id: str) -> tuple[dict, dict]:
        ob = self.observe()
        for e in ob["elements"]:
            if e.get("id") == element_id:
                return e, ob
        raise AssertionError("element not found: " + element_id)

    def click(self, element_id: str):
        e, ob = self.element(element_id)
        if not e.get("enabled", True):
            raise AssertionError("disabled: " + element_id)
        self.tool("ui_click", {"applicationId": self.app, "documentId": e["documentId"], "elementId": e["elementId"], "frameId": ob["frameId"]})

    def wait(self, element_id: str, text: str | None = None, visible: bool = True, timeout_ms: int = 8000):
        args = {"applicationId": self.app, "id": element_id, "visible": visible, "timeoutMs": timeout_ms, "image": False}
        if text is not None:
            args["text"] = text
        return self.tool("ui_wait", args)

    def text(self, element_id: str) -> str:
        e, _ = self.element(element_id)
        return e.get("text", "")

    def shot(self, path: Path):
        result = self.tool("ui_observe", {"applicationId": self.app, "image": True, "limit": 16})
        images = [c["data"] for c in result["content"] if c.get("type") == "image"]
        path.write_bytes(base64.b64decode(images[0]))
        print("saved", path)

    def pause(self, seconds: float):
        # The host keeps pumping timers and app images between requests.
        time.sleep(seconds)

    def close(self):
        try:
            self.tool("runtime_close", {"applicationId": self.app})
        except Exception:
            pass
        try:
            self.process.wait(10)
        except subprocess.TimeoutExpired:
            self.process.kill()


def sample_folder(base: Path) -> Path:
    # Unicode fixture names exercise file-list rendering.
    folder = base / "資料 2026"
    (folder / "写真").mkdir(parents=True, exist_ok=True)
    files = {
        "見積書_2026-10.pdf": 1_240_000, "議事録 第3回.docx": 84_000, "予算表.xlsx": 312_000, "説明資料.pptx": 2_480_000,
        "readme.txt": 2_100, "写真/IMG_0412.jpg": 3_400_000, "写真/IMG_0413.jpg": 3_100_000,
    }
    for name, size in files.items():
        path = folder / name
        with path.open("wb") as stream:
            stream.write(os.urandom(min(size, 4096)) * (size // 4096 + 1))
            stream.truncate(size)
    return folder


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=str(ROOT / "tests" / "results" / "screens"))
    parser.add_argument("--runtime", default=str(ROOT / "runtime" / "windows-x64"))
    parser.add_argument("--miniui", default="")
    parser.add_argument("--visible", action="store_true")
    parser.add_argument("--port", default="18495")
    parser.add_argument("--linux", action="store_true")
    args = parser.parse_args()
    out = Path(args.out); out.mkdir(parents=True, exist_ok=True)
    runtime = Path(args.runtime)
    work = Path(tempfile.mkdtemp(prefix="xfer-ui-"))
    folder = sample_folder(work)
    launcher = Path(args.miniui) if args.miniui else None
    if args.linux:
        launcher = ROOT / "Xfer.sh"
    if launcher is None:
        # The test launcher and DLLs are all bundled in Xfer.
        miniui_cmd = ROOT / "tests" / "miniui.cmd"
        temp_runtime = work / "runtime"
        shutil.copytree(runtime, temp_runtime)
        shutil.copy(miniui_cmd, temp_runtime / "miniui.cmd")
        launcher = temp_runtime / "miniui.cmd"
    if args.linux:
        # The Linux host shows ui/ itself and runs the native component in its own process.
        command = [str(launcher), "--mcp", "--state-dir", str(work / "state"),
                   "--path", str(folder), "--output", str(work / "output"), "--port", args.port]
    else:
        # MiniUI loads ui/MiniUI.App.dll (Xfer's native component) from beside the start page.
        command = ["cmd", "/c", str(launcher), str(ROOT / "ui" / "index.html"), "--root", str(ROOT / "ui"),
                   "--state-dir", str(work / "state"), "--width", "1180", "--height", "760", "--mcp"]
        if not args.visible:
            command.append("--headless")
        command += ["--", "--home", str(ROOT), "--path", str(folder), "--output", str(work / "output"), "--port", args.port]
    mcp = Mcp(command, out / "ui-drive.log")
    try:
        mcp.start()
        mcp.wait("sourcePath", text=str(folder))
        mcp.pause(0.5)
        for theme in ("dark", "light"):
            mcp.click("navSend"); mcp.pause(0.3)
            mcp.shot(out / f"send-{theme}.png")
            mcp.click("navReceive"); mcp.pause(0.6)
            mcp.shot(out / f"receive-{theme}.png")
            mcp.click("navPhone")
            try:
                mcp.wait("copyUrl", timeout_ms=10000)  # the pairing QR is ready when the URL can be copied
                for _ in range(40):
                    if mcp.text("pairUrl"):
                        break
                    mcp.pause(0.25)
            except Exception as error:  # no Tailscale here: the page shows why
                print("phone:", error)
            mcp.pause(0.5)
            mcp.shot(out / f"phone-{theme}.png")
            mcp.click("themeButton"); mcp.pause(0.3)
        mcp.click("navSend"); mcp.pause(0.2)
        mcp.click("showQr")
        mcp.wait("present", visible=True)
        mcp.pause(1.0)
        mcp.shot(out / "present.png")
        mcp.click("stopQr"); mcp.pause(0.3)
        return 0
    finally:
        mcp.close()


if __name__ == "__main__":
    sys.exit(main())
