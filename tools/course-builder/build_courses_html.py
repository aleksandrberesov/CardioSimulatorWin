#!/usr/bin/env python3
"""Generate CoursesHTML.zip — the SAME courses as build_courses.py, but in
the HTML lecture format (docs/course-format.md, 2026-05-30 HTML revision):

* `.html` lecture files (front matter + HTML body),
* `<ecg pathology= lead= caption=>` custom elements (was ```ecg fences),
* HTML `<table data-quiz-id= data-editable=>` quiz blocks (was ```table),
* `<img src="assets/...">` (was ![](...)),
* KaTeX `$...$` / `$$...$$` math preserved verbatim.

Course DATA + manifest/course.txt builders are reused from build_courses.py;
only the lecture bodies are converted Markdown -> HTML and the file
extension / output zip change. UTF-8, no BOM, LF endings.
"""
import html
import os
import re
import sys
import zipfile

import build_courses as src

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_ZIP = os.path.join(HERE, "CoursesHTML.zip")


# --- Markdown -> HTML (only the subset the course bodies actually use) -----

def _protect_math(text):
    """Pull $$...$$ and $...$ spans out so inline markdown can't mangle them."""
    spans = []

    def repl(m):
        spans.append(m.group(0))
        return f"\x00M{len(spans) - 1}\x00"

    text = re.sub(r"\$\$.*?\$\$", repl, text, flags=re.S)
    text = re.sub(r"\$[^$\n]*?\$", repl, text)
    return text, spans


def _restore_math(text, spans):
    # Escape &,<,> inside math too: the browser decodes the entities back to
    # characters in the DOM text node, so KaTeX still sees `$< 120$` correctly.
    for i, s in enumerate(spans):
        text = text.replace(f"\x00M{i}\x00", html.escape(s, quote=False))
    return text


def _inline(text):
    text, spans = _protect_math(text)
    text = html.escape(text, quote=False)
    text = re.sub(r"!\[([^\]]*)\]\(([^)]+)\)",
                  lambda m: f'<img src="{m.group(2)}" alt="{m.group(1)}">', text)
    text = re.sub(r"\[([^\]]+)\]\(([^)]+)\)",
                  lambda m: f'<a href="{m.group(2)}">{m.group(1)}</a>', text)
    text = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", text)
    text = re.sub(r"(?<!\*)\*([^*\n]+)\*(?!\*)", r"<em>\1</em>", text)
    text = re.sub(r"`([^`]+)`", r"<code>\1</code>", text)
    return _restore_math(text, spans)


def _ecg_html(block):
    d = {}
    for ln in block:
        m = re.match(r"^(\w+):\s*(.*)$", ln.strip())
        if m:
            d[m.group(1)] = m.group(2).strip()
    a = f' pathology="{html.escape(d.get("pathology", ""), quote=True)}"'
    if d.get("lead"):
        a += f' lead="{html.escape(d["lead"], quote=True)}"'
    if d.get("caption"):
        a += f' caption="{html.escape(d["caption"], quote=True)}"'
    return f"<ecg{a}></ecg>"


def _table_html(block):
    sep = next((i for i, ln in enumerate(block) if ln.strip() == "---"), None)
    head = block[:sep] if sep is not None else []
    gfm = block[sep + 1:] if sep is not None else block
    hdr = {}
    for ln in head:
        m = re.match(r"^(\w+):\s*(.*)$", ln.strip())
        if m:
            hdr[m.group(1)] = m.group(2).strip()
    editable = hdr.get("editable", "").lower() == "true"
    rows = []
    for r in gfm:
        s = r.strip()
        if not s.startswith("|"):
            continue
        if "-" in s and set(s) <= set("|-: "):  # GFM separator row
            continue
        rows.append([c.strip() for c in s.strip("|").split("|")])
    attr = f' data-quiz-id="{html.escape(hdr.get("id", ""), quote=True)}"'
    if editable:
        attr += ' data-editable="true"'
    out = [f"<table{attr}>"]
    for ri, row in enumerate(rows):
        tag = "th" if ri == 0 else "td"
        cells = []
        for c in row:
            if tag == "td" and editable and c == "":
                cells.append("<td><input></td>")
            else:
                cells.append(f"<{tag}>{_inline(c)}</{tag}>")
        out.append("<tr>" + "".join(cells) + "</tr>")
    out.append("</table>")
    return "\n".join(out)


def md_to_html(body):
    lines = body.strip("\n").split("\n")
    out, i, n = [], 0, len(body.strip("\n").split("\n"))
    while i < n:
        s = lines[i].strip()
        if s.startswith("```ecg"):
            j, blk = i + 1, []
            while j < n and lines[j].strip() != "```":
                blk.append(lines[j]); j += 1
            out.append(_ecg_html(blk)); i = j + 1; continue
        if s.startswith("```table"):
            j, blk = i + 1, []
            while j < n and lines[j].strip() != "```":
                blk.append(lines[j]); j += 1
            out.append(_table_html(blk)); i = j + 1; continue
        if s == "":
            i += 1; continue
        if s == "$$":
            j, blk = i + 1, []
            while j < n and lines[j].strip() != "$$":
                blk.append(lines[j]); j += 1
            inner = "\n".join(blk)
            out.append('<div class="math-display">'
                       + html.escape("$$\n" + inner + "\n$$", quote=False)
                       + "</div>")
            i = j + 1; continue
        m = re.match(r"^(#{1,6})\s+(.*)$", s)
        if m:
            lv = len(m.group(1))
            out.append(f"<h{lv}>{_inline(m.group(2))}</h{lv}>")
            i += 1; continue
        if re.match(r"^[-*]\s+", s):
            items = []
            while i < n and re.match(r"^[-*]\s+", lines[i].strip()):
                it_lines = [re.sub(r"^[-*]\s+", "", lines[i].strip())]
                i += 1
                while i < n:
                    t = lines[i].strip()
                    if (t == "" or t.startswith("```") or t == "$$"
                            or re.match(r"^#{1,6}\s", t) or re.match(r"^[-*]\s", t)
                            or re.match(r"^\d+\.\s", t)):
                        break
                    it_lines.append(t)
                    i += 1
                items.append(_inline(" ".join(it_lines)))
            out.append("<ul>" + "".join(f"<li>{it}</li>" for it in items) + "</ul>")
            continue
        if re.match(r"^\d+\.\s+", s):
            items = []
            while i < n and re.match(r"^\d+\.\s+", lines[i].strip()):
                it_lines = [re.sub(r"^\d+\.\s+", "", lines[i].strip())]
                i += 1
                while i < n:
                    t = lines[i].strip()
                    if (t == "" or t.startswith("```") or t == "$$"
                            or re.match(r"^#{1,6}\s", t) or re.match(r"^[-*]\s", t)
                            or re.match(r"^\d+\.\s", t)):
                        break
                    it_lines.append(t)
                    i += 1
                items.append(_inline(" ".join(it_lines)))
            out.append("<ol>" + "".join(f"<li>{it}</li>" for it in items) + "</ol>")
            continue
        if re.match(r"^!\[[^\]]*\]\([^)]+\)$", s):
            out.append(_inline(s)); i += 1; continue
        para = []
        while i < n:
            t = lines[i].strip()
            if (t == "" or t.startswith("```") or t == "$$"
                    or re.match(r"^#{1,6}\s", t) or re.match(r"^[-*]\s", t)
                    or re.match(r"^\d+\.\s", t)):
                break
            para.append(t); i += 1
        out.append(f"<p>{_inline(' '.join(para))}</p>")
    return "\n".join(out)


def lecture_html(lid, order, title, md_body):
    head = (f"---\nid: {lid}\norder: {order}\n"
            f"title: {title}\nschemaVersion: 1\n---\n")
    return head + md_to_html(md_body) + "\n"


def collect_files(courses):
    files = {"manifest.txt": src.build_manifest(courses)}
    for c in courses:
        cid = c["id"]
        files[f"{cid}/course.txt"] = src.build_course_txt(c)
        asset_name, asset_text = c["asset"]
        files[f"{cid}/assets/{asset_name}"] = asset_text
        for lec in c["lectures"]:
            for lang, body_key, title_key in (("en", "body_en", "title_en"),
                                              ("ru", "body_ru", "name_ru")):
                path = f"{cid}/lectures/{lec['id']}.{lang}.html"
                files[path] = lecture_html(lec["id"], lec["order"],
                                           lec[title_key], lec[body_key])
    return files


def main():
    valid_ids = src.valid_pathology_ids()
    errors = []
    for c in src.COURSES:
        for pid in src.course_pathologies(c):
            if pid not in valid_ids:
                errors.append(f"{c['id']}: unknown pathology '{pid}'")
    if errors:
        print("VALIDATION FAILED:")
        for e in errors:
            print("  -", e)
        sys.exit(1)

    files = collect_files(src.COURSES)
    ordered = ["manifest.txt"] + sorted(p for p in files if p != "manifest.txt")
    with zipfile.ZipFile(OUT_ZIP, "w", zipfile.ZIP_DEFLATED) as zf:
        for path in ordered:
            data = files[path].encode("utf-8")  # no BOM
            assert b"\r" not in data, f"CR found in {path}"
            zf.writestr(path, data)

    total = sum(len(c["lectures"]) for c in src.COURSES)
    print(f"Wrote {OUT_ZIP}")
    print(f"  courses       : {len(src.COURSES)}")
    print(f"  lecture files : {total * 2} ({total} x en+ru), HTML")
    print(f"  zip entries   : {len(files)}")


if __name__ == "__main__":
    main()
