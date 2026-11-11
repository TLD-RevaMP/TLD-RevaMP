#!/usr/bin/env python3
"""Preview release for players: builds the checked-out (pushed) commit as TLD RevaMP <version>, e.g. 0.69.2-preview.1,
and uploads it as a GitHub pre-release with notes written for players (a markdown file), never marked latest. By
default a DRAFT (only people with write access see it; its tag is made when someone publishes it); --public publishes it
at once. The installers and the website keep pointing at the latest full release.
Same build, layout and private-string check as tools/devrelease.py; the zip's INSTALL.txt opens with a preview note
instead of the dev-build one. Multiplayer only lets the exact same version join, so a preview plays with the same
preview only.
  tools/previewrelease.py --version 0.69.2-preview.1 --title "TLD RevaMP 0.69.2 Preview 1" --notes notes.md [--public] [--dry-run]
"""
import argparse, hashlib, pathlib, re, subprocess, sys, tempfile, zipfile
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import devrelease as dr


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--version", required=True, help="e.g. 0.69.2-preview.1")
    ap.add_argument("--title", required=True)
    ap.add_argument("--notes", required=True, help="the release notes for players (markdown)")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--public", action="store_true")
    ap.add_argument("--repo")
    a = ap.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+-preview\.\d+", a.version): dr.fail("version must look like 0.69.2-preview.1")
    if dr.git("status", "--porcelain", "--untracked-files=no"): dr.fail("the checkout has uncommitted changes")
    full = dr.git("rev-parse", "HEAD"); commit = full[:7]
    base = dr.VERSION_RE.search(dr.PLUGIN_CS.read_text(encoding="utf-8")).group(2)
    tag = "v" + a.version
    notes = pathlib.Path(a.notes).read_text(encoding="utf-8")
    if not a.dry_run:
        if not dr.git("branch", "-r", "--contains", full): dr.fail(f"{commit} isn't pushed")
        repo = a.repo or re.sub(r"\.git$", "", re.split(r"github\.com[:/]", dr.git("remote", "get-url", "origin"))[-1])
        tags = subprocess.run(["gh", "api", "--paginate", f"repos/{repo}/releases", "--jq", ".[].tag_name"],
                              check=True, capture_output=True, text=True).stdout.split()
        if tag in tags: dr.fail(f"{tag} is already released (or a draft) on {repo}")
    dr.build(base, a.version, edition="player")   # for players: TLDRevamp.PlayerEdition (no developer settings, keys, overlays, bridge)
    out = dr.package(a.version, commit)
    # the dev-build note at the top of INSTALL.txt, replaced by the preview's
    with zipfile.ZipFile(out) as z: entries = {n: z.read(n) for n in z.namelist()}
    install = entries["INSTALL.txt"].decode("utf-8").split("\n\n", 1)[1]
    entries["INSTALL.txt"] = (f"PREVIEW of the next TLD RevaMP version ({a.version}): the newest fixes before the release; it\n"
                              f"may have rough edges. In multiplayer everyone needs this exact version. Back up your saves.\n\n"
                              + install).encode("utf-8")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for n, d in sorted(entries.items()): z.writestr(n, d)
    sha256 = hashlib.sha256(out.read_bytes()).hexdigest()
    body = notes.rstrip() + f"\n\nSHA-256 of {out.name}: `{sha256}`\n"
    print(f"{out} ({out.stat().st_size // 1024} KB)  sha256 {sha256}")
    if a.dry_run:
        print(body); return
    with tempfile.NamedTemporaryFile("w", suffix=".md", delete=False, encoding="utf-8") as f: f.write(body)
    subprocess.run(["gh", "release", "create", tag, str(out), "--repo", repo, "--target", full, "--prerelease",
                    "--latest=false", "--title", a.title, "--notes-file", f.name] + ([] if a.public else ["--draft"]), check=True)
    pathlib.Path(f.name).unlink()


if __name__ == "__main__":
    main()
