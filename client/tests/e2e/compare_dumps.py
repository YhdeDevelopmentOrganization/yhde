#!/usr/bin/env python3
"""Compare the project state two YHDE editors ended up with.

Usage: compare_dumps.py <a.dump.json> <b.dump.json>
Exit code 0 when both editors converged to identical state.
"""
import json
import sys


def diff(a, b, path, out, limit=80, ids=True):
    if len(out) >= limit:
        return
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(set(a) | set(b)):
            # Identity metadata is assigned when an editor opens (adopts) a
            # scene; ids are compared where both editors have adopted it.
            if k == "metadata/_yhde_id" and (not ids or k not in a or k not in b):
                continue
            if k not in a:
                out.append(f"{path}/{k}: only in B = {json.dumps(b[k])[:160]}")
            elif k not in b:
                out.append(f"{path}/{k}: only in A = {json.dumps(a[k])[:160]}")
            else:
                diff(a[k], b[k], f"{path}/{k}", out, limit, ids)
    elif a != b:
        out.append(f"{path}: A={json.dumps(a)[:200]}  B={json.dumps(b)[:200]}")


def main():
    a = json.load(open(sys.argv[1]))
    b = json.load(open(sys.argv[2]))
    problems = []
    for scene in sorted(set(a) | set(b)):
        sa, sb = a.get(scene), b.get(scene)
        if isinstance(sa, dict) and isinstance(sb, dict) and "tree" in sa and "tree" in sb:
            print(f"{scene}: A from {sa['source']}, B from {sb['source']}, {len(sa['tree'])} vs {len(sb['tree'])} nodes")
            both_adopted = sa["source"] == "editor" and sb["source"] == "editor"
            diff(sa["tree"], sb["tree"], scene, problems, ids=both_adopted)
        else:
            diff(sa, sb, scene, problems)
    props = sum(len(n["props"]) for s in a.values() if isinstance(s, dict) and "tree" in s for n in s["tree"].values())
    if problems:
        print(f"\n{len(problems)} difference(s) (showing up to 80):")
        for p in problems:
            print("  " + p)
        sys.exit(1)
    print(f"\nConverged: identical state across both editors ({props} node properties compared).")


if __name__ == "__main__":
    main()
