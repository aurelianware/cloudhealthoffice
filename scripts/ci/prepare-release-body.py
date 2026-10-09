#!/usr/bin/env python3
"""Turn a repository release-notes file into a GitHub release body.

The notes in docs/releases/ are written to be read in the repository, so they
start with an H1 title and use repository-relative links. On a release page the
title is already shown and relative links resolve against the release URL and
break. This script:

  * drops the leading H1 (the release title carries it),
  * rewrites relative Markdown links to absolute blob URLs at the given ref,
  * turns bare `docs/...md` code spans into links,
  * joins hard-wrapped paragraph and list lines so the body renders and pastes
    cleanly.

Usage: prepare-release-body.py NOTES_PATH REPO REF OUTPUT_PATH
"""

import posixpath
import re
import sys


def absolutize(target: str, notes_dir: str, base: str) -> str:
    if re.match(r"^[a-z][a-z0-9+.-]*:", target, re.I) or target.startswith("#"):
        return target
    path, _, frag = target.partition("#")
    resolved = posixpath.normpath(posixpath.join(notes_dir, path))
    return base + resolved + ("#" + frag if frag else "")


def unwrap(text: str) -> str:
    out: list[str] = []
    in_fence = False
    for line in text.split("\n"):
        stripped = line.strip()
        if stripped.startswith("```"):
            in_fence = not in_fence
            out.append(line)
            continue
        is_continuation = (
            not in_fence
            and out
            and stripped
            and line.startswith((" ", "\t"))
            and not re.match(r"^\s*([-*+]|\d+\.)\s", line)
            and out[-1].strip()
            and not out[-1].lstrip().startswith(("#", "|", "```"))
        )
        plain_continuation = (
            not in_fence
            and out
            and stripped
            and not line.startswith((" ", "\t"))
            and not re.match(r"^([-*+]|\d+\.|#|>|\||---|```)", stripped)
            and out[-1].strip()
            and not re.match(r"^(#|\||---|```)", out[-1].strip())
        )
        if is_continuation or plain_continuation:
            out[-1] = out[-1].rstrip() + " " + stripped
        else:
            out.append(line)
    return "\n".join(out)


def main() -> int:
    if len(sys.argv) != 5:
        print(__doc__, file=sys.stderr)
        return 2
    notes_path, repo, ref, output_path = sys.argv[1:]
    text = open(notes_path, encoding="utf-8").read()

    lines = text.split("\n")
    if lines and lines[0].startswith("# "):
        text = "\n".join(lines[1:]).lstrip("\n")

    base = f"https://github.com/{repo}/blob/{ref}/"
    notes_dir = posixpath.dirname(notes_path)

    text = re.sub(
        r"\]\(([^)\s]+)\)",
        lambda m: "](" + absolutize(m.group(1), notes_dir, base) + ")",
        text,
    )
    # Bare `docs/...md` spans not already inside a link.
    text = re.sub(
        r"(?<!\[)`(docs/[^`\s]+\.md)`(?!\])",
        lambda m: f"[`{m.group(1)}`]({base}{m.group(1)})",
        text,
    )
    text = unwrap(text)

    with open(output_path, "w", encoding="utf-8") as fh:
        fh.write(text.rstrip("\n") + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
