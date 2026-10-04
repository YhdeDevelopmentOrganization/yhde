"""Packs client/godot/addons/yhde into yhde-addon-<version>.zip, ready to
upload on the server's admin page (onboarding.md).

    python client/package_addon.py [output folder] [--sign private_key.pem]
    python client/package_addon.py --new-key folder

Build the native core first (it goes into addons/yhde/bin/). Every platform
whose library is in bin/ is included; Godot's temporary copies (~lib…) and
editor caches are left out.

--sign adds addons/yhde/release.sig: an RSA signature over the version and
every file's SHA-256 (security.md, "Add-on and Server Updates"). Editors only
install updates signed by a key in updater.gd's RELEASE_KEYS, whichever server
hands them out. --new-key makes a key pair once: keep the private key off every
server and out of every repository, and put the public key in RELEASE_KEYS.
"""
import hashlib
import pathlib
import re
import sys
import zipfile

SIG = "addons/yhde/release.sig"


def digest(version, files):
    """What is signed; updater.gd's release_text() builds the same text."""
    lines = ["yhde-addon-release 1", "version " + version]
    for name in sorted(files):
        if name != SIG:
            lines.append(hashlib.sha256(files[name]).hexdigest() + "  " + name)
    return ("\n".join(lines) + "\n").encode("utf-8")


def new_key(folder):
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.primitives.asymmetric import rsa

    folder = pathlib.Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    private = folder / "yhde-release-private.pem"
    if private.exists():
        sys.exit(f"{private} already exists: refusing to overwrite a release key.")
    key = rsa.generate_private_key(public_exponent=65537, key_size=4096)
    private.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                          serialization.NoEncryption()))
    public = key.public_key().public_bytes(serialization.Encoding.PEM,
                                           serialization.PublicFormat.SubjectPublicKeyInfo).decode()
    (folder / "yhde-release-public.pem").write_text(public, encoding="utf-8")
    print(f"Private key: {private}  (back it up offline; never upload or commit it)")
    print("Public key, for RELEASE_KEYS in client/godot/addons/yhde/ui/updater.gd:\n" + public)


def sign(key_file, version, files):
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import padding

    key = serialization.load_pem_private_key(pathlib.Path(key_file).read_bytes(), password=None)
    import base64
    return base64.b64encode(key.sign(digest(version, files), padding.PKCS1v15(), hashes.SHA256())) + b"\n"


def main(argv):
    if argv[:1] == ["--new-key"]:
        if len(argv) != 2:
            sys.exit("usage: package_addon.py --new-key folder")
        new_key(argv[1])
        return
    key_file = None
    if "--sign" in argv:
        i = argv.index("--sign")
        if i + 1 >= len(argv):
            sys.exit("--sign needs the private key file")
        key_file = argv[i + 1]
        argv = argv[:i] + argv[i + 2:]

    here = pathlib.Path(__file__).resolve().parent
    addon = here / "godot" / "addons" / "yhde"
    cfg = (addon / "plugin.cfg").read_text(encoding="utf-8")
    version = re.search(r'^version="([^"]+)"', cfg, re.M).group(1)
    libs = [p.name for p in (addon / "bin").glob("libyhde.*") if not p.name.startswith("~")]
    if not libs:
        sys.exit("No native core in addons/yhde/bin/: build it first (see client/README.md).")

    out_dir = pathlib.Path(argv[0]) if argv else here
    out = out_dir / f"yhde-addon-{version}.zip"
    skip = ("join.cfg", "release.sig")
    files = {}
    for f in sorted(addon.rglob("*")):
        if f.is_dir() or f.name in skip or f.name.startswith("~") or f.suffix in (".old", ".tmp"):
            continue
        files["addons/yhde/" + f.relative_to(addon).as_posix()] = f.read_bytes()
    if key_file:
        files[SIG] = sign(key_file, version, files)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for name in sorted(files):
            z.writestr(name, files[name])
    signed = "signed" if key_file else "NOT signed: editors will refuse it as an update"
    print(f"{out}  (version {version}; {signed}; native core: {', '.join(sorted(libs))})")


if __name__ == "__main__":
    main(sys.argv[1:])
