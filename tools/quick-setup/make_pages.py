#!/usr/bin/env python3
"""Writes docs/quick-setup/<guide>.md, the English page of every guide in docs/quick-setup/steps.json.

Run it after changing steps.json; tools/quick-setup/check.py fails the build while a page is stale.
Usage: make_pages.py [repo root]
"""
import json
import os
import sys

HEADER = "<!-- Written by tools/quick-setup/make_pages.py from steps.json: edit steps.json, then run it. -->\n\n"


def pages(root):
    """{file name: content} for every guide."""
    guide_dir = os.path.join(root, "docs", "quick-setup")
    with open(os.path.join(guide_dir, "steps.json"), encoding="utf-8") as f:
        doc = json.load(f)
    lang = "en"
    out = {}
    for g in doc["guides"]:
        lines = [HEADER + "# " + g["title"][lang], "", g["subtitle"][lang], ""]
        others = [o for o in doc["guides"] if o["id"] != g["id"]]
        lines.append("Other guides: " + ", ".join("[" + o["title"][lang] + "](" + o["id"] + ".md)" for o in others) +
                     ". Every guide is also in " + str(len(doc["languages"]) - 1) + " more languages in [steps.json](steps.json).")
        for n, sid in enumerate(g["steps"], 1):
            s = doc["steps"][sid]
            lines += ["", "## " + str(n) + ". " + s["title"][lang], ""]
            if "phase" in s:
                lines += ["*" + s["phase"][lang] + "*", ""]
            image = doc["imagePath"].replace("{lang}", lang).replace("{step}", sid)
            lines += ["![" + s["alt"][lang] + "](" + image + ")", "", s["body"][lang]]
            if "tip" in s:
                lines += ["", "> " + s["tip"][lang]]
            if "cta" in s:
                lines += ["", "[" + s["cta"]["label"][lang] + "](" + s["cta"]["href"] + ")"]
        out[g["id"] + ".md"] = "\n".join(lines) + "\n"
    return out


def main(root):
    for name, content in pages(root).items():
        with open(os.path.join(root, "docs", "quick-setup", name), "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
        print("docs/quick-setup/" + name)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
