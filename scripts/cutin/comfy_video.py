"""Queue the "Yu-Gi-Oh cut-in" ComfyUI workflow (WAN 2.2 I2V + SVI 2.0 PRO, 2 s) and download the video.

    python scripts/cutin/comfy_video.py --image <start.png> --idea "<what happens>" --out <video.mp4>
        [--global "<subject / style / camera>"] [--seed N] [--megapixels 0.88] [--dlss "1.5x (Quality)"]

The API copy of the workflow is .cursor/skills/monster-cutin/workflows/yugioh_cutin_api.json (the UI copy,
for loading in ComfyUI, is "Yu-Gi-Oh cut-in.json" beside it). Defaults: 720p (0.88 MP) + DLSS 1.5x ->
1920x1080, 33 frames at 16 fps -> 65 frames at 32 fps after RIFE (2.03 s), about 100 s on an RTX 5080.
Use --megapixels 0.38 --dlss "3x (Ultra Performance)" for the faster 480p version (~50 s, softer detail).
The prompt writer stays off (mode 1): GLOBAL PROMPT + idea go to WAN as written.
ComfyUI URL: COMFYUI_URL (default http://127.0.0.1:8188).
"""
import argparse
import json
import os
import random
import sys
import time
import urllib.parse
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
API_WORKFLOW = os.path.join(REPO, ".cursor", "skills", "monster-cutin", "workflows", "yugioh_cutin_api.json")
COMFY = os.environ.get("COMFYUI_URL", "http://127.0.0.1:8188").rstrip("/")

# node ids in the workflow (checked against class_type before queueing)
NODES = {"image": ("15", "LoadImage"), "size": ("16", "ImageScaleToTotalPixels"), "seed": ("18", "PrimitiveInt"),
         "segments": ("19", "PrimitiveInt"), "global": ("20", "PrimitiveStringMultiline"), "length": ("26", "WanImageToVideoSVIPro"),
         "dlss": ("53", "DLSS5Settings"), "idea": ("59", "PrimitiveStringMultiline"), "mode": ("60", "PrimitiveInt"),
         "final": ("56", "VHS_VideoCombine")}


def http(path, data=None, headers=None, method=None):
    req = urllib.request.Request(COMFY + path, data=data, headers=headers or {}, method=method)
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read()


def upload(path, name):
    boundary = uuid.uuid4().hex
    body = b"".join([
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"{name}\"\r\nContent-Type: image/png\r\n\r\n".encode(),
        open(path, "rb").read(),
        f"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"overwrite\"\r\n\r\ntrue\r\n--{boundary}--\r\n".encode()])
    return json.loads(http("/upload/image", body, {"Content-Type": f"multipart/form-data; boundary={boundary}"}))["name"]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--image", required=True, help="16:9 start image (see scene_still.py)")
    ap.add_argument("--idea", required=True, help="the motion: what the character does in these 2 s")
    ap.add_argument("--global", dest="global_prompt", help="replaces the workflow's GLOBAL PROMPT (subject, style, camera)")
    ap.add_argument("--seed", type=int, default=None)
    ap.add_argument("--megapixels", type=float, default=None)
    ap.add_argument("--dlss", default=None, help='DLSS mode, e.g. "1.5x (Quality)" or "3x (Ultra Performance)"')
    ap.add_argument("--out", required=True, help="where to save the final 32 fps mp4")
    ap.add_argument("--workflow", default=API_WORKFLOW)
    a = ap.parse_args()

    p = json.load(open(a.workflow, encoding="utf-8"))
    for key, (nid, cls) in NODES.items():
        if p.get(nid, {}).get("class_type") != cls:
            sys.exit(f"workflow node {nid} is not {cls} ({key}); re-export yugioh_cutin_api.json")
    seed = a.seed if a.seed is not None else random.randint(1, 2 ** 48)
    # unique name: ComfyUI reads the file when the job runs, so a shared name lets a later upload replace
    # the start image of a job that is still queued
    p["15"]["inputs"]["image"] = upload(a.image, f"yugioh_cutin_{uuid.uuid4().hex[:12]}_{os.path.basename(a.image)}")
    p["18"]["inputs"]["value"] = seed
    p["19"]["inputs"]["value"] = 1
    p["60"]["inputs"]["value"] = 1
    p["59"]["inputs"]["value"] = a.idea
    if a.global_prompt:
        p["20"]["inputs"]["value"] = a.global_prompt
    if a.megapixels:
        p["16"]["inputs"]["megapixels"] = a.megapixels
    if a.dlss:
        p["53"]["inputs"]["upscaling_mode"] = a.dlss
    prefix = "YugiohCutin/" + os.path.splitext(os.path.basename(a.out))[0]
    p["56"]["inputs"]["filename_prefix"] = prefix

    t0 = time.time()
    res = json.loads(http("/prompt", json.dumps({"prompt": p, "client_id": "cutin-" + uuid.uuid4().hex[:8]}).encode(),
                          {"Content-Type": "application/json"}))
    if res.get("node_errors"):
        sys.exit(f"ComfyUI rejected the workflow: {json.dumps(res['node_errors'])[:1500]}")
    pid = res["prompt_id"]
    print(f"queued {pid} (seed {seed})", flush=True)
    while True:
        h = json.loads(http(f"/history/{pid}")).get(pid)
        if h and h.get("status", {}).get("completed") is not None and h["status"].get("status_str") in ("success", "error"):
            break
        time.sleep(3)
    if h["status"]["status_str"] != "success":
        errs = [m for m in h["status"].get("messages", []) if m[0] == "execution_error"]
        sys.exit(f"ComfyUI job failed: {json.dumps(errs)[:1500]}")
    item = h["outputs"]["56"]["gifs"][0]
    q = urllib.parse.urlencode({"filename": item["filename"], "subfolder": item["subfolder"], "type": item["type"]})
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    open(a.out, "wb").write(http(f"/view?{q}"))
    json.dump({"seed": seed, "idea": a.idea, "global": p["20"]["inputs"]["value"], "megapixels": p["16"]["inputs"]["megapixels"],
               "dlss": p["53"]["inputs"]["upscaling_mode"], "comfy_file": item["filename"], "seconds": round(time.time() - t0, 1)},
              open(os.path.splitext(a.out)[0] + ".json", "w", encoding="utf-8"), indent=1)
    print(f"done in {time.time() - t0:.0f} s -> {a.out}")


if __name__ == "__main__":
    main()
