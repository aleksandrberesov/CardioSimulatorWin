#!/usr/bin/env python3
"""Reorganize a CardioSimulator course bundle into the topic (Тема/Подтема) format.

The app's course format (docs/course-format.md + Core/Domain/CourseParser.cs) is
backward compatible: a course is a flat list of `lecture:` lines, and an *optional*
set of `topic:` lines groups them:

    topic:<id>;title:<EN>;name:<RU>            <- a GROUP Тема (holds Подтемы)
    topic:<id>;title:<EN>;name:<RU>;leaf:true  <- a LEAF Тема (is itself a lecture)
    lecture:<id>;title:<EN>;name:<RU>;topic:<topicId>   <- a Подтема of that group

This tool applies a declarative *plan* (reorg_plan.json) to an existing bundle:
per course it adds the topics, files each lecture under its topic, turns chosen
lectures into leaf Темы, and deletes leftover junk. It rewrites `<course>/course.txt`
and fixes the `lectures:` count in `manifest.txt`. Every other file is copied
byte-for-byte. Text it writes is UTF-8, no BOM, LF.

Works on a .zip bundle OR an extracted courses directory (e.g. the app's
%LOCALAPPDATA%\\CardioSimulator\\courses). Courses not named in the plan are left
exactly as they were.

Usage:
    python reorg_courses.py --in Courses.zip                 # -> Courses.reorg.zip
    python reorg_courses.py --in Courses.zip --out New.zip
    python reorg_courses.py --in <courses-dir> --out <dir>   # write reorganized tree
    python reorg_courses.py --in <courses-dir> --in-place    # edit the folder in place
    python reorg_courses.py --in Courses.zip --dry-run       # report, write nothing

A LEAF Тема's HTML content lives at lectures/<topicId>.<lang>.html. Point
`content_from` at the lecture whose files should become that content: if it equals
the topic id the files are reused as-is, otherwise they are renamed (and their
front-matter `id:` patched) to match the new topic id.
"""
import argparse
import io
import os
import re
import sys
import zipfile

sys.stdout.reconfigure(encoding="utf-8")  # print Cyrillic safely on Windows consoles


# ── course.txt parse / serialize (mirrors Core/Domain/CourseParser.cs) ──────────

def parse_semicolon(line):
    fields = {}
    for part in line.split(";"):
        i = part.find(":")
        if i <= 0:
            continue
        fields[part[:i].strip()] = part[i + 1:].strip()
    return fields


def parse_course_txt(text):
    """Return (header_text, topics, lectures). Header = the key:value lines before the
    first blank line, kept verbatim; body = the topic:/lecture: lines after it."""
    lines = [ln.rstrip("\r") for ln in text.split("\n")]
    header, i = [], 0
    while i < len(lines):
        if lines[i].strip() == "":
            i += 1
            break
        header.append(lines[i])
        i += 1
    topics, lectures = [], []
    for ln in lines[i:]:
        if ln.strip() == "":
            continue
        f = parse_semicolon(ln)
        tid, lid = f.get("topic"), f.get("lecture")
        if lid is None and tid is not None:
            topics.append({"id": tid, "title": f.get("title", ""), "name": f.get("name"),
                           "leaf": (f.get("leaf", "").lower() in ("true", "1"))})
        elif lid is not None:
            lectures.append({"id": lid, "title": f.get("title", ""), "name": f.get("name"),
                             "topic": tid})
    return "\n".join(header), topics, lectures


def serialize_course(header_text, topics, lectures):
    out = [header_text.rstrip("\n"), ""]
    for t in topics:
        s = f"topic:{t['id']};title:{t['title']}"
        if t.get("name"):
            s += f";name:{t['name']}"
        if t.get("leaf"):
            s += ";leaf:true"
        out.append(s)
    if topics and lectures:
        out.append("")
    for l in lectures:
        s = f"lecture:{l['id']};title:{l['title']}"
        if l.get("name"):
            s += f";name:{l['name']}"
        if l.get("topic"):
            s += f";topic:{l['topic']}"
        out.append(s)
    return "\n".join(out) + "\n"


# ── lecture-file helpers (operate on a path->bytes map) ─────────────────────────

def _stem(path):
    return path.rsplit("/", 1)[-1].split(".")[0]


def _lecture_keys(files, course, lecture_id):
    prefix = f"{course}/lectures/"
    return [k for k in files if k.startswith(prefix) and _stem(k) == lecture_id]


def _patch_front_matter_id(data, new_id):
    """Rewrite the `id:` line inside a lecture's `--- ... ---` front matter."""
    parts = data.decode("utf-8").split("\n")
    if not parts or parts[0].strip() != "---":
        return data
    for i in range(1, len(parts)):
        if parts[i].strip() == "---":
            break
        if parts[i].rstrip("\r").strip().startswith("id:"):
            cr = "\r" if parts[i].endswith("\r") else ""
            parts[i] = f"id: {new_id}{cr}"
            break
    return "\n".join(parts).encode("utf-8")


def _rename_lecture(files, order, course, old, new):
    prefix = f"{course}/lectures/"
    for k in _lecture_keys(files, course, old):
        rest = k.rsplit("/", 1)[-1][len(old):]          # ".en.html", ".ru.answers.json", ...
        nk = f"{prefix}{new}{rest}"
        data = files.pop(k)
        if k.endswith(".html"):
            data = _patch_front_matter_id(data, new)
        files[nk] = data
        if k in order:
            order[order.index(k)] = nk
        else:
            order.append(nk)


def _delete_lecture(files, order, course, lecture_id):
    doomed = _lecture_keys(files, course, lecture_id)
    for k in doomed:
        files.pop(k, None)
        if k in order:
            order.remove(k)
    return doomed


# ── the reorg ───────────────────────────────────────────────────────────────────

def reorg_course(files, order, course, plan, log):
    key = f"{course}/course.txt"
    if key not in files:
        log.append(f"  ! course '{course}' not found in bundle — skipped")
        return None

    header, _old_topics, lectures = parse_course_txt(files[key].decode("utf-8"))

    del_lect = set(plan.get("delete_lectures", []))
    del_top = set(plan.get("delete_topics", []))
    plan_topics = plan.get("topics", [])
    assign = plan.get("assign", {})

    # 1. drop junk lectures (+ their files)
    for lid in sorted(del_lect):
        removed = _delete_lecture(files, order, course, lid)
        note = f"({len(removed)} files)" if removed else "(no files)"
        log.append(f"  - delete lecture '{lid}' {note}")
    lectures = [l for l in lectures if l["id"] not in del_lect]
    for tid in sorted(del_top):
        log.append(f"  - delete topic '{tid}'")

    # 2. leaf Темы: their content comes from an existing lecture's files
    leaf_consumed = set()
    for t in plan_topics:
        if not t.get("leaf"):
            continue
        cf = t.get("content_from") or t["id"]
        leaf_consumed.add(cf)
        if t["id"] != cf:
            _rename_lecture(files, order, course, cf, t["id"])
            log.append(f"  ~ leaf '{t['id']}' content_from '{cf}' (files renamed)")
        else:
            log.append(f"  ~ leaf '{t['id']}' content_from '{cf}' (files reused)")
    lectures = [l for l in lectures if l["id"] not in leaf_consumed]

    # 3. new topic set from the plan
    new_topics = [{"id": t["id"], "title": t.get("title", ""), "name": t.get("name"),
                   "leaf": bool(t.get("leaf"))} for t in plan_topics]
    group_ids = {t["id"] for t in new_topics if not t["leaf"]}

    # 4. file each surviving lecture under its assigned group (else keep a still-valid
    #    group, else leave it ungrouped)
    for l in lectures:
        if l["id"] in assign:
            tgt = assign[l["id"]]
            if tgt not in group_ids:
                log.append(f"  ! lecture '{l['id']}' assigned to '{tgt}', which is not a group topic")
            l["topic"] = tgt
        elif l["topic"] not in group_ids:
            if l["topic"]:
                log.append(f"  ! lecture '{l['id']}' referenced topic '{l['topic']}' (gone) — now ungrouped")
            l["topic"] = None

    files[key] = serialize_course(header, new_topics, lectures).encode("utf-8")

    # 5. validate leaf content + report
    for t in new_topics:
        if t["leaf"] and not any(k.endswith(".html") for k in _lecture_keys(files, course, t["id"])):
            log.append(f"  ! leaf topic '{t['id']}' has NO content .html at lectures/{t['id']}.<lang>.html")
    ungrouped = [l["id"] for l in lectures if not l["topic"]]
    if ungrouped:
        log.append(f"  · {len(ungrouped)} ungrouped lecture(s): {', '.join(ungrouped)}")

    content_count = len(lectures) + sum(1 for t in new_topics if t["leaf"])
    log.append(f"  = {len(new_topics)} topics, {len(lectures)} lectures, {content_count} content items")
    return content_count


def update_manifest(files, counts, log):
    key = "manifest.txt"
    if key not in files or not counts:
        return
    lines = files[key].decode("utf-8").split("\n")
    for i, ln in enumerate(lines):
        for cid, cnt in counts.items():
            if ln.startswith(f"course:{cid};"):
                lines[i] = re.sub(r"(;lectures:)\d+", lambda m: m.group(1) + str(cnt), ln, count=1)
                log.append(f"  manifest: {cid} lectures:{cnt}")
    files[key] = "\n".join(lines).encode("utf-8")


# ── load / save (zip or directory) ──────────────────────────────────────────────

def load(path):
    if os.path.isdir(path):
        files, order = {}, []
        for root, _dirs, names in os.walk(path):
            for n in names:
                rel = os.path.relpath(os.path.join(root, n), path).replace(os.sep, "/")
                with open(os.path.join(root, n), "rb") as f:
                    files[rel] = f.read()
                order.append(rel)
        return files, order, "dir"
    with zipfile.ZipFile(path) as z:
        order = [i.filename for i in z.infolist() if not i.is_dir()]
        files = {n: z.read(n) for n in order}
    return files, order, "dir" if False else "zip"


def save(path, files, order, kind, removed):
    keys = [k for k in order if k in files] + [k for k in files if k not in order]
    if kind == "zip":
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as out:
            for k in keys:
                out.writestr(k, files[k])
        with open(path, "wb") as f:
            f.write(buf.getvalue())
    else:
        for k in keys:
            dst = os.path.join(path, k.replace("/", os.sep))
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            with open(dst, "wb") as f:
                f.write(files[k])
        for k in removed:                 # honor deletions when editing a dir in place
            p = os.path.join(path, k.replace("/", os.sep))
            if os.path.exists(p):
                os.remove(p)


# ── main ─────────────────────────────────────────────────────────────────────────

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser(description="Reorganize a course bundle into the topic format.")
    ap.add_argument("--in", dest="src", required=True, help="input .zip or courses directory")
    ap.add_argument("--out", dest="dst", help="output path (default: <in>.reorg.zip, or required for a dir)")
    ap.add_argument("--plan", default=os.path.join(here, "reorg_plan.json"), help="plan JSON (default: reorg_plan.json)")
    ap.add_argument("--in-place", action="store_true", help="write back over the input")
    ap.add_argument("--dry-run", action="store_true", help="report changes, write nothing")
    args = ap.parse_args()

    import json
    with open(args.plan, encoding="utf-8") as f:
        plan = json.load(f)
    courses_plan = plan.get("courses", {})

    files, order, kind = load(args.src)
    original = set(files)

    print(f"Loaded {len(files)} entries from {args.src} ({kind})")
    counts = {}
    for course, cplan in courses_plan.items():
        if isinstance(cplan, dict) and course in {k.split('/')[0] for k in files}:
            log = [f"[{course}]"]
            c = reorg_course(files, order, course, cplan, log)
            if c is not None:
                counts[course] = c
            print("\n".join(log))
    mlog = []
    update_manifest(files, counts, mlog)
    if mlog:
        print("\n".join(mlog))

    removed = original - set(files)
    if not counts:
        print("No planned courses were present in the bundle — nothing to do.")
        return
    if args.dry_run:
        print(f"\nDry run: {len(counts)} course(s) would change, {len(removed)} file(s) removed. Nothing written.")
        return

    if args.in_place:
        dst = args.src
    elif args.dst:
        dst = args.dst
    elif kind == "zip":
        base, ext = os.path.splitext(args.src)
        dst = base + ".reorg" + ext
    else:
        ap.error("directory input needs --out <dir> or --in-place")
    save(dst, files, order, kind, removed)
    print(f"\nWrote {dst}  ({len(counts)} course(s) reorganized, {len(removed)} file(s) removed)")


if __name__ == "__main__":
    main()
