"""Install, restore and remove custom Monster Cut-ins (summon animations) in Master Duel.

Data-only patch: no .exe/.dll changes. Background: docs/summon-animation-research.md.
Every installed cut-in is recorded in a registry, so after a game update overwrites the patched
files ONE command puts all of them back:

    python scripts/cutin/monster_cutin.py restore

Commands
  status                       what is registered and whether the game files still carry it
  find-card NAME               look up card ids in database.db (and whether they already have a cut-in)
  extract-art --card ID --out PNG
  extract-cutin --card ID --out DIR
                               a cut-in's skeleton JSON, atlas and textures (Konami's originals even
                               after an override); render them with spine_preview/serve.py
  apply --card ID --content DIR --preview-approved [--override-official]
                               install a custom cut-in built by scene_kit.py (only after the user
                               approved <DIR>/preview.gif). --override-official replaces a card's
                               official cut-in; Konami's files are backed up and return on `remove`
  apply --card ID --borrow SRC install an existing card's cut-in on another card
  restore                      re-apply every registered cut-in (run after each game update)
  verify                       deep offline checks of the installed bundles
  remove --card ID | --all     uninstall (restores the original game files when nothing is left)

State lives in %LOCALAPPDATA%\\Floowan\\cutins (registry.json, pristine/ backups, content/<id>/).
Every write starts from pristine copies of the two shared game files (CardIndividualData,
manifest_tcg). If the game changed one of them since we last wrote it (an update), the new file
becomes the pristine copy before the cut-ins are re-applied. Close the game before running.
"""
import argparse
import glob
import hashlib
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import time
import zlib

import UnityPy

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DATA_DIR = os.environ.get("FLOOWAN_CUTIN_DATA") or os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "Floowan", "cutins")
REGISTRY = os.path.join(DATA_DIR, "registry.json")

TABLE_PATH = "Duel/ScriptableObject/CardIndividualData"
MANIFEST_NAME = "manifest_tcg"
CUTIN_DIR = "Duel/Timeline/Duel/MonsterCutIn/tcg/P{id}/"
CUTIN_ROOT = "Duel/Timeline/Duel/MonsterCutIn/tcg/P{id}/{rt}/P{id}"
RESOURCE_TYPES = ("SD", "HighEnd_HD")
# Shipped bundles use header flags 0x243 (LZ4HC block info | combined dir | padding-at-start).
# We write 0x242 (LZ4, same decoder) with LZ4 data blocks.
PACKER = (0x242, 2)
CONTENT_FILES = ("skeleton.json", "hd.atlas", "sd.atlas", "hd.png", "sd.png", "meta.json", "preview.gif", "preview_sheet.png")


# ================================================================== naming / crypto (see research doc)

def hash_name(path):
    """LocalFile.HashFileName: standard CRC32 over the UTF-8 logical path, formatted x8."""
    return "%08x" % zlib.crc32(path.replace("#/", "").encode("utf-8"))


def bundle_file(player_dir, path):
    h = hash_name(path)
    return os.path.join(player_dir, "0000", h[:2], h)


class _MdRandom:
    """YgomSystem.SimpleCrypto PRNG: .NET System.Random init, but inextp starts at 31 (not 21)."""
    MBIG, MSEED = 2147483647, 161803398

    def __init__(self, seed):
        sa = [0] * 56
        mj = self.MSEED - (self.MBIG if seed == -2147483648 else abs(seed))
        sa[55], mk = mj, 1
        for i in range(1, 55):
            ii = (21 * i) % 55
            sa[ii] = mk
            mk = mj - mk
            if mk < 0:
                mk += self.MBIG
            mj = sa[ii]
        for _ in range(1, 5):
            for i in range(1, 56):
                sa[i] -= sa[1 + (i + 30) % 55]
                if sa[i] < 0:
                    sa[i] += self.MBIG
        self.sa, self.inext, self.inextp = sa, 0, 31

    def next_byte(self):
        self.inext = 1 if self.inext + 1 >= 56 else self.inext + 1
        self.inextp = 1 if self.inextp + 1 >= 56 else self.inextp + 1
        r = self.sa[self.inext] - self.sa[self.inextp]
        if r < 0:
            r += self.MBIG
        self.sa[self.inext] = r
        return int(r * (1.0 / self.MBIG) * 256.0) & 0xFF


def md_crypt(data, key="manifest"):
    """Manifest cipher. Symmetric: the same call encrypts and decrypts."""
    rnd = _MdRandom(sum(key.encode("utf-8")))
    return bytes(b ^ rnd.next_byte() for b in data)


# ================================================================== small helpers

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def now():
    return time.strftime("%Y-%m-%dT%H:%M:%S")


def preserve_original(reg, path):
    """Back up an existing game file before we overwrite it (cut-ins that replace an official one).
    Re-backs it up when the game has replaced it since our last write (an update)."""
    if not os.path.exists(path):
        return
    live_sha = sha256(path)
    pr = reg["pristine"].get(path)
    if reg["written"].get(path) == live_sha or (pr and pr["sha256"] == live_sha):
        return
    os.makedirs(os.path.join(DATA_DIR, "pristine"), exist_ok=True)
    rel = os.path.join("pristine", f"{os.path.basename(path)}-{live_sha[:12]}")
    shutil.copy2(path, os.path.join(DATA_DIR, rel))
    reg["pristine"][path] = {"file": rel, "sha256": live_sha, "logical": "official cut-in bundle", "taken": now()}


def write_bytes(path, data, reg, override=False):
    """Write a game file. Existing game files we did not write are only replaced with override=True,
    and are backed up first so `remove` can put them back."""
    if os.path.exists(path) and reg["written"].get(path) != sha256(path):
        if not override:
            raise RuntimeError(f"refusing to overwrite a game file we did not create: {path}")
        preserve_original(reg, path)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)
    reg["written"][path] = hashlib.sha256(data).hexdigest()


def game_running():
    if os.environ.get("FLOOWAN_CUTIN_SKIP_GAME_CHECK") == "1":   # tests against copied files only
        return False
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq masterduel.exe"], capture_output=True, text=True).stdout
        return "masterduel.exe" in out.lower()
    except OSError:
        return False


def load_registry():
    if os.path.exists(REGISTRY):
        return json.load(open(REGISTRY, encoding="utf-8"))
    return {"version": 1, "player_dir": None, "cutins": {}, "pristine": {}, "written": {}, "created": []}


def save_registry(reg):
    os.makedirs(DATA_DIR, exist_ok=True)
    tmp = REGISTRY + ".tmp"
    json.dump(reg, open(tmp, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
    os.replace(tmp, REGISTRY)


def detect_player_dir(reg, override):
    if override:
        return os.path.abspath(override)
    if reg.get("player_dir") and os.path.isdir(reg["player_dir"]):
        return reg["player_dir"]
    libs = []
    for steam in (r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam"):
        vdf = os.path.join(steam, "steamapps", "libraryfolders.vdf")
        if os.path.exists(vdf):
            libs += [p.replace("\\\\", "\\") for p in re.findall(r'"path"\s+"([^"]+)"', open(vdf, encoding="utf-8").read())]
    libs += [r"C:\Program Files (x86)\Steam", r"D:\Games\Steam", r"D:\SteamLibrary"]
    for lib in libs:
        for p in glob.glob(os.path.join(lib, "steamapps", "common", "Yu-Gi-Oh!  Master Duel", "LocalData", "*", "0000")):
            return os.path.dirname(p)
    sys.exit("Could not find Master Duel LocalData/<player>. Pass --player-dir.")


def card_name(card_id):
    try:
        row = sqlite3.connect(os.path.join(REPO, "database.db")).execute("select name from card where id = ?", (card_id,)).fetchone()
        return row[0] if row else str(card_id)
    except sqlite3.Error:
        return str(card_id)


def read_table_ids(path):
    env = UnityPy.load(path)
    for o in env.objects:
        if o.type.name == "MonoBehaviour":
            t = o.read_typetree()
            if t.get("m_Name") == "CardIndividualData":
                return [e["mrk"] for e in t["monsterCutinTable"]]
    raise RuntimeError("CardIndividualData not found")


def read_manifest_text(path):
    return md_crypt(open(path, "rb").read()).decode("utf-8")


# ================================================================== pristine (original game files)

def base_file(reg, player, logical):
    """Return the path of the unpatched version of a shared game file, refreshing the backup
    when the game has replaced the file since our last write (e.g. after an update)."""
    live = bundle_file(player, logical)
    live_sha = sha256(live)
    pr = reg["pristine"].get(live)
    if pr and os.path.exists(os.path.join(DATA_DIR, pr["file"])):
        if reg["written"].get(live) == live_sha or pr["sha256"] == live_sha:
            return os.path.join(DATA_DIR, pr["file"])
        print(f"  {logical}: changed by the game since our last write (update?) - taking a new backup")
    os.makedirs(os.path.join(DATA_DIR, "pristine"), exist_ok=True)
    rel = os.path.join("pristine", f"{os.path.basename(live)}-{live_sha[:12]}")
    shutil.copy2(live, os.path.join(DATA_DIR, rel))
    reg["pristine"][live] = {"file": rel, "sha256": live_sha, "logical": logical, "taken": now()}
    reg["written"].pop(live, None)
    return os.path.join(DATA_DIR, rel)


# ================================================================== bundle builders

def _rename_files(bf, old_cab, new_cab, drop_ress=False):
    files = {}
    for k, f in bf.files.items():
        if k.endswith(".resS"):
            if drop_ress:
                continue
            nk = k.replace(old_cab, new_cab)
        else:
            nk = new_cab
            if hasattr(f, "name"):
                f.name = nk
        files[nk] = f
    bf.files.clear()
    bf.files.update(files)


def build_borrow(player, src_id, dst_id, base_man, reg, override=False):
    """Clone SRC's two root prefab bundles as P{DST}; dependencies stay on SRC's shared bundles."""
    by_name = {e["assetName"]: e for e in base_man["informations"]}
    entries, files = [], []
    for rt in RESOURCE_TYPES:
        src_path, dst_path = CUTIN_ROOT.format(id=src_id, rt=rt), CUTIN_ROOT.format(id=dst_id, rt=rt)
        if src_path not in by_name:
            raise RuntimeError(f"card {src_id} has no cut-in ({src_path} not in manifest)")
        h = hash_name(dst_path)
        new_name = f"{h[:2]}/{h}"
        env = UnityPy.load(bundle_file(player, src_path))
        for o in env.objects:
            if o.type.name == "AssetBundle":
                t = o.read_typetree()
                t["m_Container"] = [(k.replace(f"tcg/p{src_id}/", f"tcg/p{dst_id}/").replace(f"/p{src_id}.prefab", f"/p{dst_id}.prefab"), i)
                                    for k, i in t["m_Container"]]
                t["m_Name"] = t["m_AssetBundleName"] = new_name
                o.save_typetree(t)
        old_cab = [k for k in env.file.files if not k.endswith(".resS")][0]
        _rename_files(env.file, old_cab, "CAB-" + hashlib.md5(new_name.encode()).hexdigest())
        out = bundle_file(player, dst_path)
        write_bytes(out, env.file.save(packer=PACKER), reg, override)
        files.append(out)
        entries.append({"assetName": dst_path, "dependencies": list(by_name[src_path]["dependencies"])})
    return entries, files


def anim_duration(skel):
    def walk(d):
        if isinstance(d, dict):
            return max([v if k == "time" and isinstance(v, (int, float)) else walk(v) for k, v in d.items()] or [0.0])
        if isinstance(d, list):
            return max([walk(v) for v in d] or [0.0])
        return 0.0
    return walk(skel["animations"]["animation"])


def build_custom(player, tpl_id, dst_id, content, base_man, reg, override=False):
    """Clone every P{tpl} bundle as P{dst} (new CABs, rewired externals/dependencies) and swap in
    the Spine content (skeleton JSON, atlases, BC7 textures, timeline length)."""
    from PIL import Image
    prefix = CUTIN_DIR.format(id=tpl_id)
    tpl_entries = [e for e in base_man["informations"] if e["assetName"].startswith(prefix)]
    if not tpl_entries:
        raise RuntimeError(f"template P{tpl_id} is not in the manifest (game update removed it?)")
    s_tok, d_tok = f"P{tpl_id}", f"P{dst_id}"
    ren = lambda p: p.replace(s_tok, d_tok)
    skel_text = open(os.path.join(content, "skeleton.json"), encoding="utf-8").read()
    duration = anim_duration(json.loads(skel_text))
    atlas = {rt: open(os.path.join(content, f"{rt}.atlas"), encoding="utf-8").read() for rt in ("hd", "sd")}

    info = {}
    for e in tpl_entries:
        old, new = e["assetName"], ren(e["assetName"])
        oh, nh = hash_name(old), hash_name(new)
        env = UnityPy.load(bundle_file(player, old))
        cabs = [k for k in env.file.files if not k.endswith(".resS")]
        if len(cabs) != 1:
            raise RuntimeError(f"unexpected bundle layout {old}: {list(env.file.files)}")
        info[old] = {"new": new, "oh": oh, "nh": nh, "old_cab": cabs[0], "new_cab": "CAB-" + hashlib.md5(f"{nh[:2]}/{nh}".encode()).hexdigest()}
    cab_map = {v["old_cab"]: v["new_cab"] for v in info.values()}
    name_map = {f"{v['oh'][:2]}/{v['oh']}": f"{v['nh'][:2]}/{v['nh']}" for v in info.values()}

    files = []
    for old, v in info.items():
        rel = old[len(prefix):]
        hd = rel.startswith("HighEnd_HD/")
        env = UnityPy.load(bundle_file(player, old))
        bf = env.file
        for ext in bf.files[v["old_cab"]].externals:
            for oc, nc in cab_map.items():
                if oc in ext.path:
                    ext.path = ext.path.replace(oc, nc)
        drop_ress = False
        for o in env.objects:
            tn = o.type.name
            if tn == "AssetBundle":
                t = o.read_typetree()
                t["m_Name"] = t["m_AssetBundleName"] = name_map[t["m_AssetBundleName"]]
                t["m_Dependencies"] = [name_map.get(d, d) for d in t["m_Dependencies"]]
                t["m_Container"] = [(k.replace(s_tok.lower(), d_tok.lower()), i) for k, i in t["m_Container"]]
                o.save_typetree(t)
            elif tn == "TextAsset" and (rel.endswith(f"{s_tok}JS") or rel.endswith(f"{s_tok}.atlas")):
                t = o.read_typetree()
                t["m_Script"] = skel_text if rel.endswith("JS") else atlas["hd" if hd else "sd"]
                o.save_typetree(t)
            elif tn == "Texture2D":
                t = o.read()
                t.set_image(Image.open(os.path.join(content, "hd.png" if hd else "sd.png")).convert("RGBA"),
                            target_format=t.m_TextureFormat, mipmap_count=1)
                t.save()
                drop_ress = True
            elif tn == "MonoBehaviour" and rel == f"{s_tok}Timeline":
                t = o.read_typetree()
                if t.get("m_Clips"):
                    for c in t["m_Clips"]:
                        c["m_Duration"] = float(duration)
                    o.save_typetree(t)
        _rename_files(bf, v["old_cab"], v["new_cab"], drop_ress)
        out = bundle_file(player, v["new"])
        write_bytes(out, bf.save(packer=PACKER), reg, override)
        files.append(out)
    entries = [{"assetName": ren(e["assetName"]), "dependencies": [ren(d) for d in e["dependencies"]]} for e in tpl_entries]
    return entries, files


# ================================================================== apply everything (idempotent)

def apply_all(reg, player):
    if game_running():
        sys.exit("Master Duel is running. Close it first.")
    reg["player_dir"] = player
    table_live = bundle_file(player, TABLE_PATH)
    man_live = bundle_file(player, MANIFEST_NAME)
    table_base = base_file(reg, player, TABLE_PATH)
    man_base = base_file(reg, player, MANIFEST_NAME)
    save_registry(reg)

    base_ids = set(read_table_ids(table_base))
    base_text = read_manifest_text(man_base)
    base_man = json.loads(base_text)
    base_names = {e["assetName"] for e in base_man["informations"]}

    active, replaced = {}, set()
    for cid, c in sorted(reg["cutins"].items(), key=lambda kv: int(kv[0])):
        dst = int(cid)
        official = dst in base_ids or CUTIN_ROOT.format(id=dst, rt="SD") in base_names
        if official and not c.get("override_official"):
            print(f"  ! {c.get('name', cid)} ({cid}) now has an official cut-in in the game; skipping (yours stays registered)")
            c["skipped_official"] = True
            c["files"] = []
            continue
        c.pop("skipped_official", None)
        if official:
            replaced.add(dst)      # our cut-in replaces Konami's: drop its manifest entries, keep its table entry
        active[dst] = c

    new_entries, keep = [], set()
    for dst, c in active.items():
        ov = dst in replaced
        if c["mode"] == "custom":
            entries, files = build_custom(player, c["template"], dst, os.path.join(DATA_DIR, c["content"]), base_man, reg, ov)
        else:
            entries, files = build_borrow(player, c["src"], dst, base_man, reg, ov)
        new_entries += entries
        keep.update(files)
        c["files"] = sorted(files)
        print(f"  {c.get('name', dst)} ({dst}): {len(files)} bundles")

    if active:
        env = UnityPy.load(table_base)
        for o in env.objects:
            if o.type.name == "MonoBehaviour":
                t = o.read_typetree()
                if t.get("m_Name") == "CardIndividualData":
                    t["monsterCutinTable"] += [{"mrk": d} for d in active if d not in base_ids]
                    o.save_typetree(t)
        write_bytes(table_live, env.file.save(packer=PACKER), reg, override=True)
        # The manifest JSON round-trips byte for byte through json.dumps (checked), so rebuild it:
        # our entries first, then the original ones minus the official sets we replace.
        drop = tuple(CUTIN_DIR.format(id=d) for d in replaced)
        kept = [e for e in base_man["informations"] if not (drop and e["assetName"].startswith(drop))]
        text = json.dumps({"informations": new_entries + kept}, separators=(",", ":"), ensure_ascii=False)
        write_bytes(man_live, md_crypt(text.encode("utf-8")), reg, override=True)
    else:   # nothing installed: put the original files back byte for byte
        for live, base in ((table_live, table_base), (man_live, man_base)):
            shutil.copy2(base, live)
            reg["written"].pop(live, None)
        print("  no cut-ins registered: original game files restored")

    for f in list(reg["created"]):
        if f not in keep:
            pr = reg["pristine"].get(f)
            if os.path.exists(f) and sha256(f) == reg["written"].get(f):
                if pr:      # an official cut-in file we had replaced: put Konami's version back
                    shutil.copy2(os.path.join(DATA_DIR, pr["file"]), f)
                    print(f"  restored original {f}")
                else:
                    os.remove(f)
                    print(f"  removed {f}")
            reg["created"].remove(f)
            reg["written"].pop(f, None)
    reg["created"] = sorted(set(reg["created"]) | keep)
    reg["last_apply"] = now()
    save_registry(reg)
    print(f"done: {len(active)} cut-in(s) active.")


# ================================================================== commands

def cmd_status(a, reg):
    player = detect_player_dir(reg, a.player_dir)
    print(f"player dir : {player}\ndata dir   : {DATA_DIR}")
    if not reg["cutins"]:
        print("no cut-ins registered.")
        return 0
    table_live = bundle_file(player, TABLE_PATH)
    man_live = bundle_file(player, MANIFEST_NAME)
    ids = set(read_table_ids(table_live))
    names = {e["assetName"] for e in json.loads(read_manifest_text(man_live))["informations"]}
    need = False
    for cid, c in sorted(reg["cutins"].items(), key=lambda kv: int(kv[0])):
        dst = int(cid)
        files = c.get("files", [])
        files_ok = bool(files) and all(os.path.exists(f) and sha256(f) == reg["written"].get(f) for f in files)
        ok = dst in ids and CUTIN_ROOT.format(id=dst, rt="SD") in names and files_ok
        how = f"custom (template P{c['template']})" if c["mode"] == "custom" else f"borrowed from {c['src']}"
        if c.get("override_official"):
            how += ", replaces official"
        if c.get("skipped_official"):
            print(f"  {cid:>6}  {c.get('name', ''):<32} {how:<28} SKIPPED (the game has its own cut-in)")
            continue
        need |= not ok
        print(f"  {cid:>6}  {c.get('name', ''):<32} {how:<28} {'OK' if ok else 'NEEDS RESTORE'}")
    if need:
        print("\nThe game replaced patched files (update?). Close the game and run:  monster_cutin.py restore")
    return 1 if need else 0


def cmd_find_card(a, reg):
    db = os.path.join(REPO, "database.db")
    rows = sqlite3.connect(db).execute("select id, name, card_type from card where name like ? order by id", (f"%{a.name}%",)).fetchall()
    player = detect_player_dir(reg, a.player_dir)
    pr = reg["pristine"].get(bundle_file(player, TABLE_PATH))
    official = set(read_table_ids(os.path.join(DATA_DIR, pr["file"]) if pr else bundle_file(player, TABLE_PATH)))
    for cid, name, ctype in rows[:40]:
        tag = "official cut-in" if cid in official else ("custom cut-in" if str(cid) in reg["cutins"] else "")
        print(f"{cid:>6}  {name:<50} {ctype or '':<18} {tag}")


def cmd_extract_art(a, reg):
    db = os.path.join(REPO, "database.db")
    row = sqlite3.connect(db).execute("select bundle, name from card where id = ?", (a.card,)).fetchone()
    if not row:
        sys.exit(f"card {a.card} not in database.db")
    player = detect_player_dir(reg, a.player_dir)
    install = os.path.dirname(os.path.dirname(os.path.dirname(player)))
    for root in (os.path.join(player, "0000"), os.path.join(install, "masterduel_Data", "StreamingAssets", "AssetBundle")):
        p = os.path.join(root, row[0][:2], row[0])
        if os.path.exists(p):
            for o in UnityPy.load(p).objects:
                if o.type.name == "Texture2D":
                    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
                    o.read().image.save(a.out)
                    print(f"{row[1]} ({a.card}) art -> {a.out}")
                    return 0
    sys.exit(f"art bundle {row[0]} not found")


def cmd_apply(a, reg):
    if not a.card:
        sys.exit("--card is required")
    player = detect_player_dir(reg, a.player_dir)
    if game_running():
        sys.exit("Master Duel is running. Close it first.")
    cid = str(a.card)
    pr = reg["pristine"].get(bundle_file(player, TABLE_PATH))
    official = a.card in set(read_table_ids(os.path.join(DATA_DIR, pr["file"]) if pr else bundle_file(player, TABLE_PATH)))
    if official and not a.override_official:
        sys.exit(f"{card_name(a.card)} ({a.card}) has an official cut-in. To replace it with yours, pass --override-official "
                 "(Konami's files are backed up and come back with `remove`).")
    if a.borrow:
        entry = {"mode": "borrow", "src": a.borrow, "name": card_name(a.card), "added": now()}
    else:
        if not a.content:
            sys.exit("give --content DIR (custom) or --borrow SRC")
        if not a.preview_approved:
            sys.exit("Refusing: show <content>/preview.gif to the user and pass --preview-approved only after they approve it.")
        meta = json.load(open(os.path.join(a.content, "meta.json"), encoding="utf-8"))
        if int(meta["card_id"]) != a.card:
            sys.exit(f"content was built for card {meta['card_id']}, not {a.card}")
        missing = [f for f in CONTENT_FILES if not os.path.exists(os.path.join(a.content, f))]
        if missing:
            sys.exit(f"content folder is missing {missing}")
        dst_dir = os.path.join(DATA_DIR, "content", cid)
        os.makedirs(dst_dir, exist_ok=True)
        for f in CONTENT_FILES:
            shutil.copy2(os.path.join(a.content, f), os.path.join(dst_dir, f))
        entry = {"mode": "custom", "template": int(meta["template"]), "name": meta.get("name") or card_name(a.card),
                 "content": os.path.join("content", cid), "added": now()}
    if official:
        entry["override_official"] = True
    reg["cutins"][cid] = entry
    save_registry(reg)
    apply_all(reg, player)


def cmd_extract_cutin(a, reg):
    """Write a cut-in's HighEnd_HD skeleton JSON, atlas and page textures (Konami's originals when
    the card's cut-in has been overridden) so it can be studied or rendered with spine_preview."""
    if not (a.card and a.out):
        sys.exit("extract-cutin needs --card ID --out DIR")
    player = detect_player_dir(reg, a.player_dir)
    man_live = bundle_file(player, MANIFEST_NAME)
    pr = reg["pristine"].get(man_live)
    man = json.loads(read_manifest_text(os.path.join(DATA_DIR, pr["file"]) if pr else man_live))
    prefix = CUTIN_DIR.format(id=a.card)
    ents = [e["assetName"] for e in man["informations"] if e["assetName"].startswith(prefix)]
    if not ents:
        sys.exit(f"no cut-in for {a.card} in the manifest")
    os.makedirs(a.out, exist_ok=True)
    info = {"card_id": a.card, "files": []}
    for path in ents:
        rel = path[len(prefix):]
        if not (rel.startswith("HighEnd_HD/") or rel == f"P{a.card}Timeline"):
            continue
        live = bundle_file(player, path)
        src = os.path.join(DATA_DIR, reg["pristine"][live]["file"]) if live in reg["pristine"] else live
        for o in UnityPy.load(src).objects:
            n = o.type.name
            if n == "TextAsset":
                t = o.read()
                data = t.m_Script.encode("utf-8", "surrogateescape") if isinstance(t.m_Script, str) else bytes(t.m_Script)
                name = t.m_Name[:-2] + ".json" if t.m_Name.endswith("JS") else t.m_Name
                open(os.path.join(a.out, name), "wb").write(data)
                info["files"].append(name)
            elif n == "Texture2D":
                t = o.read()
                t.image.save(os.path.join(a.out, t.m_Name + ".png"))
                info["files"].append(t.m_Name + ".png")
            elif n == "Transform":
                sc = o.read_typetree()["m_LocalScale"]["x"]
                if abs(sc - 1) > 1e-6:
                    info["prefab_scale"] = round(sc, 4)
            elif n == "MonoBehaviour" and rel.endswith("Timeline"):
                t = o.read_typetree()
                if t.get("m_Clips"):
                    info["duration"] = round(t["m_Clips"][0]["m_Duration"], 4)
    json.dump(info, open(os.path.join(a.out, "cutin_info.json"), "w", encoding="utf-8"), indent=1)
    print(json.dumps(info, indent=1))


def cmd_restore(a, reg):
    if not reg["cutins"]:
        print("no cut-ins registered.")
        return 0
    apply_all(reg, detect_player_dir(reg, a.player_dir))


def cmd_remove(a, reg):
    player = detect_player_dir(reg, a.player_dir)
    ids = list(reg["cutins"]) if a.all else [str(a.card)]
    for cid in ids:
        c = reg["cutins"].pop(cid, None)
        if c is None:
            print(f"  {cid} is not registered")
        elif c["mode"] == "custom":
            shutil.rmtree(os.path.join(DATA_DIR, c["content"]), ignore_errors=True)
    save_registry(reg)
    apply_all(reg, player)


def cmd_verify(a, reg):
    from PIL import Image
    import numpy as np
    player = detect_player_dir(reg, a.player_dir)
    man = json.loads(read_manifest_text(bundle_file(player, MANIFEST_NAME)))
    ids = set(read_table_ids(bundle_file(player, TABLE_PATH)))
    ok = True
    for cid, c in reg["cutins"].items():
        if c.get("skipped_official"):
            print(f"  {cid:>6} {c.get('name', ''):<32} SKIPPED (the game has its own cut-in)")
            continue
        ents = [e for e in man["informations"] if f"/P{cid}/" in e["assetName"]]
        cabs = {}
        for e in ents:
            env = UnityPy.load(bundle_file(player, e["assetName"]))
            cabs[[k for k in env.file.files if not k.endswith(".resS")][0]] = e["assetName"]
        bad = []
        for e in ents:
            f = bundle_file(player, e["assetName"])
            env = UnityPy.load(f)
            sf = [v for k, v in env.file.files.items() if not k.endswith(".resS")][0]
            if c["mode"] == "custom" and any(x.path.split("/")[-1] not in cabs for x in sf.externals):
                bad.append(f"{e['assetName']}: external outside set")
            if not all(os.path.exists(bundle_file(player, d)) for d in e["dependencies"]):
                bad.append(f"{e['assetName']}: missing dependency file")
            if c["mode"] == "custom":
                for o in env.objects:
                    if o.type.name == "Texture2D":
                        t = o.read()
                        ref = Image.open(os.path.join(DATA_DIR, c["content"], "hd.png" if "HighEnd_HD" in e["assetName"] else "sd.png")).convert("RGBA")
                        d = np.abs(np.asarray(t.image.convert("RGBA").resize((256, 256))).astype(int) - np.asarray(ref.resize((256, 256))).astype(int)).mean()
                        if d > 4 or t.m_StreamData.path:
                            bad.append(f"{e['assetName']}: texture mismatch ({d:.1f})")
        good = int(cid) in ids and len(ents) >= 2 and not bad
        ok &= good
        print(f"  {cid:>6} {c.get('name', ''):<32} table={int(cid) in ids} bundles={len(ents)} {'OK' if good else 'FAILED'}")
        for b in bad:
            print("         ", b)
    print("VERIFY", "OK" if ok else "FAILED")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["status", "find-card", "extract-art", "extract-cutin", "apply", "restore", "verify", "remove"])
    ap.add_argument("name", nargs="?", help="find-card: part of the card name")
    ap.add_argument("--card", type=int)
    ap.add_argument("--content", help="apply: folder produced by scene_kit.py (<work>/out)")
    ap.add_argument("--borrow", type=int, help="apply: reuse this card's existing cut-in")
    ap.add_argument("--preview-approved", action="store_true", help="apply: the user approved preview.gif")
    ap.add_argument("--override-official", action="store_true", help="apply: replace the card's official cut-in (originals are backed up)")
    ap.add_argument("--out", help="extract-art: output PNG; extract-cutin: output folder")
    ap.add_argument("--all", action="store_true", help="remove: every registered cut-in")
    ap.add_argument("--player-dir", help="LocalData/<playerId> folder (auto-detected and remembered)")
    a = ap.parse_args()
    reg = load_registry()
    cmds = {"status": cmd_status, "find-card": cmd_find_card, "extract-art": cmd_extract_art, "extract-cutin": cmd_extract_cutin, "apply": cmd_apply,
            "restore": cmd_restore, "verify": cmd_verify, "remove": cmd_remove}
    if a.command == "remove" and not (a.all or a.card):
        sys.exit("remove needs --card ID or --all")
    return cmds[a.command](a, reg) or 0


if __name__ == "__main__":
    sys.exit(main())
