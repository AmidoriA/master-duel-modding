"""Serve a cut-in to render.html (real Spine 4.2 web runtime) and collect the rendered frames.

    python scripts/cutin/spine_preview/serve.py --content <dir> --skeleton P16386.json --atlas P16386.atlas \
        --prefab-scale 0.17 [--fps 30] [--port 8765]

Open http://127.0.0.1:<port>/ in a browser (e.g. the Claude browser pane). Frames are written to
<content>/runtime_frames/NNN.png; when the page reports done, the server writes runtime_preview.mp4 (exact
colours), runtime_preview.gif and runtime_preview_sheet.png into <content> and exits. Works for shipped
cut-ins (extracted JSON/atlas/pages) and for scene_kit output (skeleton.json + hd.atlas + hd.png; pass --page-alias P19172.png=hd.png).
"""
import argparse
import base64
import json
import os
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--content", required=True)
    ap.add_argument("--skeleton", required=True)
    ap.add_argument("--atlas", required=True)
    ap.add_argument("--prefab-scale", type=float, default=0.24, help="prefab transform scale (0.24 for our template P19172)")
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--ortho", type=float, default=2.9, help="effective in-game camera size (calibrated; see scene_kit.GAME_ORTHO_SIZE)")
    ap.add_argument("--duration", type=float, default=0, help="seconds to render (default: animation length)")
    ap.add_argument("--page-alias", action="append", default=[], help="NAME=FILE: serve FILE when the atlas asks for NAME")
    ap.add_argument("--port", type=int, default=8765)
    a = ap.parse_args()
    content = os.path.abspath(a.content)
    frames_dir = os.path.join(content, "runtime_frames")
    os.makedirs(frames_dir, exist_ok=True)
    for f in os.listdir(frames_dir):
        os.remove(os.path.join(frames_dir, f))
    alias = dict(x.split("=", 1) for x in a.page_alias)
    cfg = {"skeleton": a.skeleton, "atlas": a.atlas, "fps": a.fps, "duration": a.duration,
           "viewHeightUnits": 2 * a.ortho / (0.01 * a.prefab_scale)}
    done = threading.Event()

    class H(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def _send(self, body, ctype):
            self.send_response(200)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            p = self.path.split("?")[0]
            if p in ("/", "/index.html"):
                return self._send(open(os.path.join(HERE, "render.html"), "rb").read(), "text/html; charset=utf-8")
            if p == "/config.json":
                return self._send(json.dumps(cfg).encode(), "application/json")
            if p.startswith("/c/"):
                name = os.path.basename(p[3:])
                fp = os.path.join(content, alias.get(name, name))
                if os.path.isfile(fp):
                    ctype = "image/png" if fp.endswith(".png") else "text/plain; charset=utf-8"
                    return self._send(open(fp, "rb").read(), ctype)
            self.send_error(404)

        def do_POST(self):
            body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
            if self.path.startswith("/frame"):
                i = int(self.path.split("i=")[1])
                data = body.decode().split(",", 1)[1]
                open(os.path.join(frames_dir, f"{i:03d}.png"), "wb").write(base64.b64decode(data))
                self._send(b"ok", "text/plain")
            elif self.path.startswith("/done"):
                self._send(b"ok", "text/plain")
                print("page:", body.decode(), flush=True)
                done.set()
            else:
                self.send_error(404)

    srv = ThreadingHTTPServer(("127.0.0.1", a.port), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    print(f"serving http://127.0.0.1:{a.port}/  (content: {content})", flush=True)
    done.wait()
    srv.shutdown()
    from PIL import Image
    files = sorted(f for f in os.listdir(frames_dir) if f.endswith(".png"))
    if not files:
        sys.exit("no frames rendered")
    frames = [Image.open(os.path.join(frames_dir, f)).convert("RGB") for f in files]
    sys.path.insert(0, os.path.dirname(HERE))
    import scene_kit                      # same writer as the kit preview: exact-colour mp4 + stable-palette gif
    scene_kit.save_previews(frames, content, a.fps, "runtime_preview")
    print(f"wrote {len(frames)} frames, runtime_preview.mp4/.gif, runtime_preview_sheet.png", flush=True)


if __name__ == "__main__":
    main()
