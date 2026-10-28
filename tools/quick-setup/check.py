#!/usr/bin/env python3
"""Checks docs/quick-setup/steps.json, the data other sites read to show PAB's setup guides.

- every guide and step has its texts in every listed language;
- every step a guide names exists, and every step has its picture in every language
  (docs/quick-setup/images/<lang>/<step>.png, drawn by tools/quick-setup/render.mjs);
- every app text a step quotes ("appText") is still in the tray app's source, so renaming a menu
  item fails the build until the guide says the new name;
- every guide's English page (docs/quick-setup/<guide>.md) is what make_pages.py writes.

Usage: check.py [repo root]. Exit code 0 when all is well, 1 with the problems listed otherwise.
"""
import json
import os
import sys

TEXT_FIELDS = ("phase", "short", "title", "body", "tip", "alt")
REQUIRED_STEP_FIELDS = ("short", "title", "body", "alt", "appText")


def main(root):
    guide_dir = os.path.join(root, "docs", "quick-setup")
    with open(os.path.join(guide_dir, "steps.json"), encoding="utf-8") as f:
        doc = json.load(f)
    problems = []

    def texts(where, value):
        if not isinstance(value, dict):
            problems.append(where + ": not a {language: text} object")
            return
        for lang in doc["languages"]:
            if not isinstance(value.get(lang), str) or not value[lang].strip():
                problems.append(where + ": no " + lang + " text")
        for lang in value:
            if lang not in doc["languages"]:
                problems.append(where + ": " + lang + " is not in \"languages\"")

    if doc.get("schemaVersion") != 1:
        problems.append("schemaVersion is not 1: update this check with the schema")
    if doc.get("defaultLanguage") not in doc.get("languages", []):
        problems.append("defaultLanguage is not one of \"languages\"")
    for key, value in doc.get("ui", {}).items():
        texts("ui." + key, value)

    steps = doc.get("steps", {})
    used = set()
    ids = set()
    for g in doc.get("guides", []):
        gid = g.get("id")
        if not gid or gid in ids:
            problems.append("guide without an id, or a repeated id: " + repr(gid))
        ids.add(gid)
        texts("guides." + str(gid) + ".title", g.get("title"))
        texts("guides." + str(gid) + ".subtitle", g.get("subtitle"))
        if not g.get("steps"):
            problems.append("guides." + str(gid) + ": no steps")
        for s in g.get("steps", []):
            used.add(s)
            if s not in steps:
                problems.append("guides." + str(gid) + ": step " + s + " does not exist")

    sources = tray_sources(root)
    for sid, step in steps.items():
        if sid not in used:
            problems.append("steps." + sid + ": no guide uses it")
        for field in REQUIRED_STEP_FIELDS:
            if field not in step:
                problems.append("steps." + sid + ": no " + field)
        for field in TEXT_FIELDS:
            if field in step:
                texts("steps." + sid + "." + field, step[field])
        cta = step.get("cta")
        if cta is not None:
            if not str(cta.get("href", "")).startswith("https://"):
                problems.append("steps." + sid + ".cta.href: not an https:// link")
            texts("steps." + sid + ".cta.label", cta.get("label"))
        for quoted in step.get("appText", []):
            if not any(quoted in src for src in sources):
                problems.append("steps." + sid + ": the app no longer says \"" + quoted + "\" (src/Tray): update the guide")
        for lang in doc["languages"]:
            image = doc["imagePath"].replace("{lang}", lang).replace("{step}", sid)
            if not os.path.isfile(os.path.join(guide_dir, *image.split("/"))):
                problems.append("steps." + sid + ": no picture " + image + " (run tools/quick-setup/render.mjs)")

    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import make_pages
    for name, content in make_pages.pages(root).items():
        path = os.path.join(guide_dir, name)
        current = open(path, encoding="utf-8").read() if os.path.isfile(path) else None
        if current != content:
            problems.append("docs/quick-setup/" + name + " does not match steps.json (run tools/quick-setup/make_pages.py)")

    for p in problems:
        print("quick-setup: " + p, file=sys.stderr)
    if not problems:
        print("quick-setup: " + str(len(doc["guides"])) + " guides, " + str(len(steps)) + " steps, " +
              str(len(doc["languages"])) + " languages: OK")
    return 1 if problems else 0


def tray_sources(root):
    out = []
    for folder, _, files in os.walk(os.path.join(root, "src", "Tray")):
        for name in files:
            if name.endswith(".cs"):
                with open(os.path.join(folder, name), encoding="utf-8") as f:
                    out.append(f.read())
    return out


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")))
