#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Auto-tag the bundled rhythm dataset with a single primary taxonomy acronym per record.

The dataset's titles are compound finding-lists ("Sinus Bradycardia + Left ventricular
hypertrophy + T wave Change"). Each finding phrase maps to a taxonomy acronym; the record's own
authored `group:` is the ground truth for which finding is *primary* — we pick the mapped finding whose
taxonomy group matches the record group (earliest-in-title wins ties), falling back to the most
clinically significant finding when none matches.

Pipeline:
  ContentPacker cat <Pathologies.pak> manifest.txt > manifest.txt
  python tools/taxonomy-build/build_rhythm_acronyms.py manifest.txt > tools/taxonomy-build/rhythm_acronyms.tsv
  ContentPacker apply-acronyms <in.pak> tools/taxonomy-build/rhythm_acronyms.tsv <out.pak>

Emits `id<TAB>acronym` lines (stdout). A coverage summary goes to stderr.
"""

import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TAXONOMY_TSV = os.path.join(HERE, "taxonomy.tsv")

# Finding-phrase → acronym. Patterns are scanned longest/most-specific first so "Complete right
# bundle branch block" wins over "right bundle branch block" over "Bundle branch block". Case-insensitive.
# Each entry: (regex, acronym, fallback_priority)  — priority only breaks ties when NO finding matches
# the record's group (higher = more clinically significant).
RULES = [
    # ── Infarction localization (most specific first) ──
    (r"anteroseptal MI",        "MI_ANTSEP",   100),
    (r"anterolateral MI",       "MI_ANTLAT",   100),
    (r"anterior MI",            "MI_ANT",      100),
    (r"posterior MI",           "MI_INF_POST", 100),
    (r"lateral MI",             "MI_LAT",      100),
    (r"inferior MI|lower wall", "MI_INF",      100),
    (r"\bMI\b",                 "MI",           98),
    (r"abnormal Q wave",        "AQW",          52),
    (r"fQRS",                   "FQRS",         50),
    # ── Malignant / specific ventricular ──
    (r"ventricular fibrillation",           "VFIB", 95),
    (r"ventricular flutter",                "VFL",  95),
    (r"paroxysmal ventricular tachycardia", "PVT",  92),
    (r"ventricular tachycardia",            "PVT",  90),
    (r"idioventricular rhythm",             "VER",  35),
    # ── Pre-excitation / channelopathy syndromes ──
    (r"ventricular preexcitation",          "VPE",  85),
    (r"wpw",                                "WPW",  85),
    (r"early repolarization",               "ERV",  85),
    (r"brugada",                            "BRUG", 85),
    # ── Tachyarrhythmias / SVT ──
    (r"atrial fibrillation",                "AFIB", 80),
    (r"atrial flutter",                     "AF",   80),
    (r"supraventricular tachycardia",       "SVT",  80),
    (r"av nodal reentr|reentrant tachycardia", "AVNRT", 80),
    (r"atrial tachycardia",                 "AT",   78),
    # ── Ectopy ──
    (r"atrial premature beats|atrial premature",  "APB", 65),
    (r"premature ventricular contractions",       "PVC", 66),
    (r"ventricular premature beat",               "VPB", 65),
    (r"ventricular escape beat",                  "VEB", 64),
    (r"junctional escape|nodal escape",           "JEB", 64),
    (r"pacemaker migration|wandering pacemaker",  "WAVN", 33),
    # ── Conduction (specific first) ──
    (r"2 degree.*mobitz ii|mobitz ii",            "2AVB2", 76),
    (r"2 degree.*type one|mobitz i\b",            "2AVB1", 76),
    (r"2 degree atrioventricular block|2° av block", "2AVB", 76),
    (r"3.? av block|3 degree atrioventricular",   "3AVB",  77),
    (r"1 degree atrioventricular block|1° av block", "1AVB", 75),
    (r"PR interval extension",                    "PRIE",  74),
    (r"complete right bundle branch block",       "CRBBB", 75),
    (r"incomplete right bundle branch block",     "IRBBB", 73),
    (r"complete left bundle branch block",        "CLBBB", 75),
    (r"incomplete left bundle branch block",      "ILBBB", 73),
    (r"right bundle branch block",                "RBBB",  74),
    (r"left bundle branch block",                 "LBBB",  74),
    (r"left anterior fascicular block|left front bundle branch block", "LAFB", 74),
    (r"left posterior fascicular block",          "LPFB",  74),
    (r"bundle branch block",                      "BBB",   72),
    (r"intraventricular block",                   "IDC",   71),
    (r"sinoatrial block",                         "SAB",   75),
    # ── Pacing ──
    (r"artificial pacing|pacemaker|pacing rhythm|sequental pacing|stimulation of the ventricles", "APACE", 70),
    # ── Hypertrophy / chamber ──
    (r"left ventricular hypertrophy|left ventricle hypertrophy", "LVH", 60),
    (r"right ventric",                            "RVH",  60),
    (r"right atrial hypertrophy|right atrial enlarge", "RAH", 60),
    (r"left atrial hypertrophy|left atrial enlarge",   "LAH", 60),
    (r"tall p wave",                              "TPW",  58),
    (r"prolonged p wave",                         "PPW",  58),
    (r"\bp wave change",                          "PWC",  40),
    # ── QT / electrolyte ──
    (r"qt interval extension|prolongation of the qt", "QTIE", 55),
    (r"hypocalcemia",                             "QTIE", 55),
    (r"hypercalcemia",                            "SQTI", 55),
    (r"hypokalemia|u wave",                       "UW",   55),
    # ── Ischemia signs ──
    (r"wellens",                                  "TWO",  52),
    (r"st-t change",                              "STTC", 50),
    (r"st segment changes",                       "STC",  50),
    (r"st drop down|st depression",               "STDD", 50),
    (r"st extension|st elevation",                "STTU", 50),
    (r"t wave opposite",                          "TWO",  49),
    (r"t wave change",                            "TWC",  48),
    (r"\bafter ischemia",                         "TWC",  48),
    (r"\bischemia",                               "STDD", 48),
    (r"r wave changes",                           "RWC",  40),
    # ── Axis / rotation / voltage ──
    (r"axis left shift",                          "ALS",  40),
    (r"axis right shift",                         "ARS",  40),
    (r"counter.?colockwise rotation|counterclockwise rotation", "CCR", 40),
    (r"colockwise rotation|clockwise rotation",   "CR",   40),
    (r"lower voltage qrs",                        "LVQRSAL", 40),
    (r"PR interval shorten|short pr",             "SPRI", 40),
    # ── Ectopic / atrial escape rhythms ──
    (r"coronary sinus rhythm|atrial rhythm|ectopic rhythm", "ARHY", 34),
    (r"atrioventricular rhythm",                  "JEB",  34),
    # ── Base sinus rhythms (least significant as the single primary tag) ──
    (r"sinus bradycardia",                        "SB",   20),
    (r"sinus tachycardia",                        "ST",   20),
    (r"sinus (irregularity|arrythmia|arrhythmia)","SA",   20),
    (r"sinus rhythm|sinus rhytm",                 "SR",   18),
]


def load_taxonomy_groups(path):
    """acronym → group key, from the generated taxonomy.tsv."""
    groups = {}
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line or line[0] == "#":
                continue
            c = line.split("\t")
            if len(c) < 3 or c[0].strip().lower() == "acronym":
                continue
            groups[c[0].strip().upper()] = c[2].strip()
    return groups


COMPILED = [(re.compile(rx, re.IGNORECASE), acr, prio) for rx, acr, prio in RULES]


def classify(title, record_group, tax_groups):
    """Returns (acronyms, reason) — every finding in the title, primary first — or ([], reason).
    Primary = the finding whose taxonomy group matches the record group (earliest match wins), else the
    most clinically significant finding. The rest follow in title order."""
    # Collect every rule match with its text span.
    matches = []  # (acronym, start, end, priority)
    for rx, acr, prio in COMPILED:
        m = rx.search(title)
        if m:
            matches.append((acr, m.start(), m.end(), prio))

    # Drop any match whose span nests inside a longer match's span — removes hierarchical sub-phrase
    # redundancy: "RBBB"/"BBB" inside "Complete right bundle branch block", "lateral MI"/"MI" inside
    # "anterolateral MI". Distinct findings (separated by '+') sit at different spans and survive.
    seen = {}  # acronym -> (position, priority)
    for i, (acr, s, e, prio) in enumerate(matches):
        nested = any(
            j != i and sj <= s and e <= ej and (ej - sj) > (e - s)
            for j, (_, sj, ej, _) in enumerate(matches)
        )
        if nested:
            continue
        if acr not in seen or s < seen[acr][0]:
            seen[acr] = (s, prio)
    if not seen:
        return [], "no-finding"

    group_matches = [
        (pos, prio, acr) for acr, (pos, prio) in seen.items()
        if tax_groups.get(acr) == record_group
    ]
    if group_matches:
        group_matches.sort(key=lambda t: t[0])  # earliest in title
        primary, reason = group_matches[0][2], "group-match"
    else:
        # No finding sits in the record's group — take the most significant finding.
        primary, reason = max(seen.items(), key=lambda kv: kv[1][1])[0], "priority-fallback"

    rest = sorted((acr for acr in seen if acr != primary), key=lambda a: seen[a][0])
    return [primary] + rest, reason


def main():
    if len(sys.argv) < 2:
        print("usage: build_rhythm_acronyms.py <manifest.txt>", file=sys.stderr)
        return 2
    manifest_path = sys.argv[1]
    tax_groups = load_taxonomy_groups(TAXONOMY_TSV)

    rows, tagged, untagged, fallback = [], 0, 0, 0
    per_group = {}
    with open(manifest_path, "r", encoding="utf-8") as f:
        for raw in f:
            line = raw.strip()
            if not line.startswith("pathology:"):
                continue
            fields = {}
            for part in line.split(";"):
                i = part.find(":")
                if i > 0:
                    fields[part[:i].strip()] = part[i + 1:].strip()
            pid = fields.get("pathology")
            title = fields.get("title", "")
            group = fields.get("group", "")
            if not pid:
                continue
            acronyms, reason = classify(title, group, tax_groups)
            if acronyms:
                rows.append((pid, acronyms))
                tagged += 1
                if reason == "priority-fallback":
                    fallback += 1
                per_group[group] = per_group.get(group, 0) + 1
            else:
                untagged += 1
                print(f"  UNTAGGED {pid}: {title!r} (group={group})", file=sys.stderr)

    print("# id\tacronyms (comma-separated, primary first) — generated by build_rhythm_acronyms.py")
    for pid, acronyms in rows:
        print(f"{pid}\t{','.join(acronyms)}")

    total_codes = sum(len(a) for _, a in rows)
    print(f"\nTagged {tagged} rhythms with {total_codes} codes "
          f"(avg {total_codes / max(1, tagged):.1f}/rhythm), untagged {untagged} "
          f"({fallback} via priority-fallback).", file=sys.stderr)
    print("Per record-group: " + ", ".join(f"{k}:{v}" for k, v in sorted(per_group.items())), file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
