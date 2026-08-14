#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Build the canonical ECG acronym taxonomy (taxonomy.tsv) from the three customer
source tables. This is the single join spine that wires rhythms, lectures, test
questions and student results together.

Sources (under CardioSimulator/Docs/):
  1. "таблица соответсвия общая.txt"          — master rows: acronym -> group / subsection / section
  2. "Группировка по подразделам.txt"          — reverse index: subsection -> acronyms (cross-check)
  3. "Акронимы инфарктов (детальная привязка).txt" — the MI_* family under 6.3 (not in table 1)
  4. "справочник ритмов.xlsx"                   — the customer's master reference; source of truth for
                                                  the English name (name_en, used as the base for other
                                                  languages). Requires the 'openpyxl' package.

Output columns (tab-separated, one row per acronym):
  acronym  name_ru  group  section  subsection  subsection_title  alt_subsections  name_en

- group is the groups.txt key (sinus/arrhythmia/conduction/... ) so the taxonomy
  reuses the vocabulary the app already ships.
- subsection is the PRIMARY node (e.g. "4.6.2"); alt_subsections is a ';'-joined
  list of the extra nodes for acronyms that map to more than one (e.g. WPW -> 8.1).
- section is derived from the primary subsection's leading number so it can never
  disagree with subsection.

Run:  python tools/taxonomy-build/build_taxonomy.py
Emits: tools/taxonomy-build/taxonomy.tsv AND src/CardioSimulator.Core/Domain/Taxonomy.tsv
"""

import os
import sys
import re

HERE = os.path.dirname(os.path.abspath(__file__))
WIN_ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
DOCS = os.path.abspath(os.path.join(WIN_ROOT, "..", "Docs"))

SRC_GENERAL = os.path.join(DOCS, "таблица соответсвия общая.txt")
SRC_GROUPING = os.path.join(DOCS, "Группировка по подразделам.txt")
SRC_INFARCT = os.path.join(DOCS, "Акронимы инфарктов (детальная привязка).txt")
SRC_XLSX = os.path.join(DOCS, "справочник ритмов.xlsx")

OUT_TOOL = os.path.join(HERE, "taxonomy.tsv")
OUT_CORE = os.path.join(WIN_ROOT, "src", "CardioSimulator.Core", "Domain", "Taxonomy.tsv")

# RU group label (table 1 "Группа" column) -> groups.txt key.
GROUP_KEY = {
    "Синусовый": "sinus",
    "Нарушения ритма": "arrhythmia",
    "Нарушения проводимости": "conduction",
    "Гипертрофия": "hypertrophy",
    "Ишемия": "ischemia",
    "Инфаркт": "infarction",
    "Электролитные/токсические": "electrolyte",
    "Синдромы/каналопатии": "syndromes",
    "ЭКС": "pacemaker",
    "Особые/ось/вольтаж": "special",
}


def read_lines(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return [ln.rstrip("\n").rstrip("\r") for ln in f]


def load_en_names():
    """acronym (upper) -> English name, read from the customer's xlsx source of truth.
    Column layout: A=Акроним, B=Название (EN), C=Название (RU), D=Группа, ..."""
    try:
        import openpyxl  # noqa: WPS433 (local import: only needed when regenerating)
    except ImportError:
        print("ERROR: reading English names needs the 'openpyxl' package.\n"
              "       Install it with:  pip install openpyxl", file=sys.stderr)
        raise
    wb = openpyxl.load_workbook(SRC_XLSX, data_only=True, read_only=True)
    ws = wb.active
    names = {}
    for row in ws.iter_rows(min_row=2, values_only=True):
        if not row or row[0] is None:
            continue
        acronym = str(row[0]).strip().upper()
        if not acronym:
            continue
        en = str(row[1]).strip() if len(row) > 1 and row[1] is not None else ""
        names[acronym] = en
    wb.close()
    return names


def cells(line):
    return [c.strip() for c in line.split("\t")]


def split_subsections(raw):
    """'6.1 / 6.2' -> ('6.1', ['6.2']). Keeps only tokens that look like N(.N)*."""
    parts = [p.strip() for p in re.split(r"[/,]", raw) if p.strip()]
    nums = [p for p in parts if re.match(r"^\d+(\.\d+)*$", p)]
    if not nums:
        return raw.strip(), []
    return nums[0], nums[1:]


def section_of(subsection):
    m = re.match(r"^(\d+)", subsection)
    return int(m.group(1)) if m else 0


class Entry:
    __slots__ = ("acronym", "name_ru", "group", "section", "subsection", "title", "alt", "name_en")

    def __init__(self, acronym, name_ru, group, subsection, title, alt):
        self.acronym = acronym
        self.name_ru = name_ru
        self.group = group
        self.subsection = subsection
        self.title = title
        self.alt = alt
        self.section = section_of(subsection)
        self.name_en = ""  # filled from the xlsx source of truth in main()


def parse_general(entries, warnings):
    lines = read_lines(SRC_GENERAL)
    for ln in lines[1:]:  # skip header row
        if not ln.strip():
            continue
        c = cells(ln)
        if len(c) < 5:
            continue
        acronym = c[0].strip().upper()
        if not acronym:
            continue
        name_ru = c[1].strip()
        group_ru = c[2].strip()
        subsection, alt = split_subsections(c[3])
        title = c[4].strip()
        group = GROUP_KEY.get(group_ru)
        if group is None:
            warnings.append(f"unknown group '{group_ru}' for {acronym}; left blank")
            group = ""
        entries[acronym] = Entry(acronym, name_ru, group, subsection, title, alt)


def parse_infarct(entries, warnings):
    lines = read_lines(SRC_INFARCT)
    # File starts with a title line, then a header row, then data.
    for ln in lines:
        if not ln.strip():
            continue
        c = cells(ln)
        if len(c) < 4:
            continue
        acronym = c[0].strip().upper()
        # skip the title/header lines
        if acronym in ("АКРОНИМ", "") or acronym.startswith("АКРОНИМЫ ИНФАРКТОВ"):
            continue
        if not re.match(r"^[A-Z0-9_]+$", acronym):
            continue
        name_ru = c[1].strip()
        subsection, alt = split_subsections(c[2])
        title = c[3].strip()
        if acronym in entries:
            # already covered by the general table; keep it, just fill a missing title
            if not entries[acronym].title and title:
                entries[acronym].title = title
            continue
        entries[acronym] = Entry(acronym, name_ru, "infarction", subsection, title, alt)


def crosscheck_grouping(entries, warnings):
    """Table 2 lists subsection -> acronyms. Warn on any acronym it names that we
    did not capture (a data gap worth surfacing), ignoring the 'все MI_*' shorthand."""
    lines = read_lines(SRC_GROUPING)
    named = set()
    for ln in lines:
        if "\t" not in ln:
            continue
        c = cells(ln)
        if len(c) < 2 or c[0].strip() in ("Подраздел", ""):
            continue
        for tok in re.split(r"[,\n]", c[1]):
            tok = tok.strip().strip("+").strip()
            tok = tok.split(" ")[0].strip().upper()
            if re.match(r"^[A-Z0-9_]+$", tok):
                named.add(tok)
    missing = sorted(t for t in named if t not in entries and not t.startswith("MI_"))
    for t in missing:
        warnings.append(f"grouping table names '{t}' but it is absent from the taxonomy")


def main():
    for p in (SRC_GENERAL, SRC_GROUPING, SRC_INFARCT, SRC_XLSX):
        if not os.path.exists(p):
            print(f"ERROR: source not found: {p}", file=sys.stderr)
            return 1

    entries = {}
    warnings = []
    parse_general(entries, warnings)
    parse_infarct(entries, warnings)
    crosscheck_grouping(entries, warnings)

    # English name (name_en) comes from the xlsx source of truth; it is the base other
    # languages localize from, so warn on any acronym the xlsx does not name.
    en_names = load_en_names()
    for acronym, e in entries.items():
        e.name_en = en_names.get(acronym, "")
        if not e.name_en:
            warnings.append(f"no English name in the xlsx for '{acronym}'; name_en left blank")

    rows = sorted(entries.values(), key=lambda e: (e.section, e.subsection, e.acronym))

    header = "acronym\tname_ru\tgroup\tsection\tsubsection\tsubsection_title\talt_subsections\tname_en"
    out = [
        "# Canonical ECG acronym taxonomy — GENERATED by tools/taxonomy-build/build_taxonomy.py.",
        "# Do not edit by hand; edit the source tables under CardioSimulator/Docs/ and re-run.",
        "# Columns: acronym  name_ru  group  section  subsection  subsection_title  alt_subsections  name_en",
        header,
    ]
    for e in rows:
        out.append("\t".join([
            e.acronym, e.name_ru, e.group, str(e.section),
            e.subsection, e.title, ";".join(e.alt), e.name_en,
        ]))
    text = "\n".join(out) + "\n"

    for target in (OUT_TOOL, OUT_CORE):
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)

    print(f"Wrote {len(rows)} acronyms -> {OUT_TOOL}")
    print(f"                       -> {OUT_CORE}")
    by_section = {}
    for e in rows:
        by_section.setdefault(e.section, 0)
        by_section[e.section] += 1
    print("Per section:", ", ".join(f"§{k}:{v}" for k, v in sorted(by_section.items())))
    if warnings:
        print(f"\n{len(warnings)} warning(s):")
        for w in warnings:
            print("  -", w)
    return 0


if __name__ == "__main__":
    sys.exit(main())
