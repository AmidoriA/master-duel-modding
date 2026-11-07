"""Proof of concept: give card DST the Monster Cut-in (summon animation) of card SRC.

Data-only patch for Master Duel (no .exe/.dll changes). See docs/summon-animation-research.md.

What `apply` does:
  1. Back up CardIndividualData (60c98f0d) and manifest_tcg (70e201b4) once.
  2. Append {mrk: DST} to CardIndividualData.monsterCutinTable.
  3. Clone the two root prefab bundles of SRC (SD + HighEnd_HD) as P{DST}: new container path,
     new bundle name (crc32 of the logical path) and a new CAB name. Dependencies stay on SRC's bundles.
  4. Add manifest_tcg entries for the two new paths, copying SRC's dependency lists.

`revert` restores the backups and deletes the cloned bundles.

Usage (needs UnityPy):
  python scripts/poc/monster_cutin_poc.py apply  --src 4007 --dst 12418
  python scripts/poc/monster_cutin_poc.py verify --src 4007 --dst 12418
  python scripts/poc/monster_cutin_poc.py revert
"""
import argparse
import hashlib
import json
import os
import shutil
import sys
import zlib

import UnityPy

DEFAULT_PLAYER = r"D:\Games\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel\LocalData\2addcbb5"
# Outside the repo so game files never end up in a commit.
DEFAULT_BACKUP = os.path.join(
    os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "Floowan", "cutin-poc-backup")

TABLE_PATH = "Duel/ScriptableObject/CardIndividualData"
# Match the shipped bundles: header flags 0x243 = LZ4HC block info | combined dir | padding-at-start.
# We write 0x242 (LZ4 instead of LZ4HC, same decoder) with LZ4 data blocks.
PACKER = (0x242, 2)
MANIFEST_NAME = "manifest_tcg"
CUTIN_ROOT = "Duel/Timeline/Duel/MonsterCutIn/tcg/P{id}/{rt}/P{id}"
RESOURCE_TYPES = ("SD", "HighEnd_HD")


# ---------------------------------------------------------------- naming / crypto

def hash_name(path: str) -> str:
    """LocalFile.HashFileName: standard CRC32 over UTF-8 path, formatted x8."""
    return "%08x" % zlib.crc32(path.replace("#/", "").encode("utf-8"))


def bundle_file(player_dir: str, path: str) -> str:
    h = hash_name(path)
    return os.path.join(player_dir, "0000", h[:2], h)


class _MdRandom:
    """YgomSystem.SimpleCrypto PRNG: .NET System.Random init, but inextp starts at 31 (not 21)."""
    MBIG = 2147483647
    MSEED = 161803398

    def __init__(self, seed: int):
        sa = [0] * 56
        mj = self.MSEED - (self.MBIG if seed == -2147483648 else abs(seed))
        sa[55] = mj
        mk = 1
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

    def next_byte(self) -> int:
        self.inext = 1 if self.inext + 1 >= 56 else self.inext + 1
        self.inextp = 1 if self.inextp + 1 >= 56 else self.inextp + 1
        r = self.sa[self.inext] - self.sa[self.inextp]
        if r < 0:
            r += self.MBIG
        self.sa[self.inext] = r
        return int(r * (1.0 / self.MBIG) * 256.0) & 0xFF


def md_crypt(data: bytes, key: str = "manifest") -> bytes:
    """Symmetric: the same call encrypts and decrypts."""
    rnd = _MdRandom(sum(key.encode("utf-8")))
    return bytes(b ^ rnd.next_byte() for b in data)


# ---------------------------------------------------------------- helpers

def asset_bundle_obj(env):
    for o in env.objects:
        if o.type.name == "AssetBundle":
            return o
    raise RuntimeError("no AssetBundle object")


def load_state(backup_dir):
    p = os.path.join(backup_dir, "state.json")
    return json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {"backups": {}, "created": []}


def save_state(backup_dir, state):
    os.makedirs(backup_dir, exist_ok=True)
    json.dump(state, open(os.path.join(backup_dir, "state.json"), "w", encoding="utf-8"), indent=1)


def backup_once(state, backup_dir, live_path):
    if live_path in state["backups"]:
        return
    dst = os.path.join(backup_dir, os.path.basename(live_path))
    shutil.copy2(live_path, dst)
    state["backups"][live_path] = dst
    print(f"  backed up {live_path} -> {dst}")


# ---------------------------------------------------------------- steps

def patch_table(player_dir, dst_id):
    path = bundle_file(player_dir, TABLE_PATH)
    env = UnityPy.load(path)
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        t = o.read_typetree()
        if t.get("m_Name") != "CardIndividualData":
            continue
        if any(e["mrk"] == dst_id for e in t["monsterCutinTable"]):
            print(f"  table already has {dst_id}")
            return
        t["monsterCutinTable"].append({"mrk": dst_id})
        o.save_typetree(t)
        data = env.file.save(packer=PACKER)
        open(path, "wb").write(data)
        print(f"  table: appended {dst_id} ({len(t['monsterCutinTable'])} entries) -> {path}")
        return
    raise RuntimeError("CardIndividualData MonoBehaviour not found")


def clone_root(player_dir, src_id, dst_id, rt, state):
    src_path = CUTIN_ROOT.format(id=src_id, rt=rt)
    dst_path = CUTIN_ROOT.format(id=dst_id, rt=rt)
    src_file = bundle_file(player_dir, src_path)
    dst_file = bundle_file(player_dir, dst_path)
    if not os.path.exists(src_file):
        raise FileNotFoundError(src_file)
    if os.path.exists(dst_file) and dst_file not in state["created"]:
        raise RuntimeError(f"refusing to overwrite a game file we did not create: {dst_file}")

    h = hash_name(dst_path)
    new_bundle_name = f"{h[:2]}/{h}"
    env = UnityPy.load(src_file)
    abo = asset_bundle_obj(env)
    t = abo.read_typetree()
    s_lo, d_lo = f"tcg/p{src_id}/", f"tcg/p{dst_id}/"
    s_pf, d_pf = f"/p{src_id}.prefab", f"/p{dst_id}.prefab"
    new_cont = []
    for key, info in t["m_Container"]:
        nk = key.replace(s_lo, d_lo).replace(s_pf, d_pf)
        new_cont.append((nk, info))
    t["m_Container"] = new_cont
    t["m_Name"] = new_bundle_name
    t["m_AssetBundleName"] = new_bundle_name
    abo.save_typetree(t)

    # Rename the CAB so the clone can be loaded alongside the source cut-in.
    bf = env.file
    old_names = list(bf.files.keys())
    if len(old_names) != 1 or not old_names[0].startswith("CAB-"):
        raise RuntimeError(f"unexpected bundle layout: {old_names}")
    new_cab = "CAB-" + hashlib.md5(new_bundle_name.encode()).hexdigest()
    sf = bf.files.pop(old_names[0])
    if hasattr(sf, "name"):
        sf.name = new_cab
    bf.files[new_cab] = sf

    os.makedirs(os.path.dirname(dst_file), exist_ok=True)
    open(dst_file, "wb").write(bf.save(packer=PACKER))
    if dst_file not in state["created"]:
        state["created"].append(dst_file)
    print(f"  cloned {src_path} -> {dst_path}  [{os.path.basename(src_file)} -> {h}, {new_cab}]")


def patch_manifest(player_dir, src_id, dst_id):
    path = bundle_file(player_dir, MANIFEST_NAME)
    text = md_crypt(open(path, "rb").read()).decode("utf-8")
    j = json.loads(text)
    by_name = {e["assetName"]: e for e in j["informations"]}
    head = '{"informations":['
    if not text.startswith(head):
        raise RuntimeError("unexpected manifest layout")
    inserts = []
    for rt in RESOURCE_TYPES:
        src_path = CUTIN_ROOT.format(id=src_id, rt=rt)
        dst_path = CUTIN_ROOT.format(id=dst_id, rt=rt)
        if dst_path in by_name:
            print(f"  manifest already has {dst_path}")
            continue
        entry = {"assetName": dst_path, "dependencies": list(by_name[src_path]["dependencies"])}
        inserts.append(json.dumps(entry, separators=(",", ":"), ensure_ascii=False))
    if not inserts:
        return
    # Insert at the front so the rest of the original JSON stays byte-identical.
    text = head + ",".join(inserts) + "," + text[len(head):]
    json.loads(text)  # sanity
    open(path, "wb").write(md_crypt(text.encode("utf-8")))
    print(f"  manifest_tcg: added {len(inserts)} entries -> {path}")


# ---------------------------------------------------------------- commands

def cmd_apply(a):
    state = load_state(a.backup_dir)
    os.makedirs(a.backup_dir, exist_ok=True)
    backup_once(state, a.backup_dir, bundle_file(a.player_dir, TABLE_PATH))
    backup_once(state, a.backup_dir, bundle_file(a.player_dir, MANIFEST_NAME))
    save_state(a.backup_dir, state)
    patch_table(a.player_dir, a.dst)
    for rt in RESOURCE_TYPES:
        clone_root(a.player_dir, a.src, a.dst, rt, state)
        save_state(a.backup_dir, state)
    patch_manifest(a.player_dir, a.src, a.dst)
    print("done. run `verify`, then test in game.")


def cmd_verify(a):
    ok = True
    env = UnityPy.load(bundle_file(a.player_dir, TABLE_PATH))
    for o in env.objects:
        if o.type.name == "MonoBehaviour":
            t = o.read_typetree()
            if t.get("m_Name") == "CardIndividualData":
                ids = [e["mrk"] for e in t["monsterCutinTable"]]
                has = a.dst in ids
                ok &= has
                print(f"table: {len(ids)} entries, has {a.dst}: {has}, mrkPowerTable {len(t['mrkPowerTable'])}")
    man = json.loads(md_crypt(open(bundle_file(a.player_dir, MANIFEST_NAME), "rb").read()).decode("utf-8"))
    names = {e["assetName"]: e for e in man["informations"]}
    print(f"manifest_tcg: {len(names)} entries")
    for rt in RESOURCE_TYPES:
        p = CUTIN_ROOT.format(id=a.dst, rt=rt)
        f = bundle_file(a.player_dir, p)
        exists = os.path.exists(f)
        in_man = p in names
        deps_ok = in_man and all(os.path.exists(bundle_file(a.player_dir, d)) for d in names[p]["dependencies"])
        detail = ""
        if exists:
            e2 = UnityPy.load(f)
            t = asset_bundle_obj(e2).read_typetree()
            detail = f" name={t['m_AssetBundleName']} cab={list(e2.file.files)[0]} cont={sorted({c[0] for c in t['m_Container']})}"
            kinds = sorted({o.type.name for o in e2.objects})
            detail += f" objs={kinds}"
        print(f"{rt}: file={exists} manifest={in_man} deps_on_disk={deps_ok}{detail}")
        ok &= exists and in_man and deps_ok
    print("VERIFY", "OK" if ok else "FAILED")
    return 0 if ok else 1


def cmd_revert(a):
    state = load_state(a.backup_dir)
    for live, bak in state["backups"].items():
        shutil.copy2(bak, live)
        print(f"  restored {live}")
    for f in state["created"]:
        if os.path.exists(f):
            os.remove(f)
            print(f"  deleted {f}")
    os.remove(os.path.join(a.backup_dir, "state.json"))
    print("reverted.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["apply", "verify", "revert"])
    ap.add_argument("--src", type=int, default=4007, help="card id that owns the cut-in (default Blue-Eyes 4007)")
    ap.add_argument("--dst", type=int, default=12418, help="card id to receive it (default Magician's Rod 12418)")
    ap.add_argument("--player-dir", default=DEFAULT_PLAYER, help="LocalData/<playerId> folder")
    ap.add_argument("--backup-dir", default=DEFAULT_BACKUP)
    a = ap.parse_args()
    return {"apply": cmd_apply, "verify": cmd_verify, "revert": cmd_revert}[a.command](a) or 0


if __name__ == "__main__":
    sys.exit(main())
