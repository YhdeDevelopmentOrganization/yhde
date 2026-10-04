"""Builds the update packages the updater test checks, each as a zip:
good (signed, newer), tampered (a file changed after signing), unsigned,
older (signed but not newer), wrong_key (signed by another key). Also writes
the test's public key to key.pem. Uses throwaway keys, never the real one.

    python make_packages.py <addon folder> <output folder>
"""
import base64
import pathlib
import re
import sys
import zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
from package_addon import SIG, digest  # noqa: E402

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa

addon = pathlib.Path(sys.argv[1])
out = pathlib.Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)

key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
other = rsa.generate_private_key(public_exponent=65537, key_size=2048)
(out / "key.pem").write_bytes(key.public_key().public_bytes(serialization.Encoding.PEM,
                                                            serialization.PublicFormat.SubjectPublicKeyInfo))


def files_with_version(version):
    files = {}
    for f in sorted(addon.rglob("*")):
        if f.is_file() and f.name != "release.sig" and not f.name.startswith("~"):
            files["addons/yhde/" + f.relative_to(addon).as_posix()] = f.read_bytes()
    cfg = files["addons/yhde/plugin.cfg"].decode()
    files["addons/yhde/plugin.cfg"] = re.sub(r'^version="[^"]*"', f'version="{version}"', cfg, flags=re.M).encode()
    return files


def signed(files, version, with_key):
    files = dict(files)
    sig = with_key.sign(digest(version, files), padding.PKCS1v15(), hashes.SHA256())
    files[SIG] = base64.b64encode(sig) + b"\n"
    return files


def write(name, files):
    with zipfile.ZipFile(out / f"{name}.zip", "w") as z:
        for n in sorted(files):
            z.writestr(n, files[n])


new = files_with_version("99.0.0")
write("good", signed(new, "99.0.0", key))
tampered = signed(new, "99.0.0", key)
tampered["addons/yhde/main.gd"] = tampered["addons/yhde/main.gd"] + b"\n# changed after signing\n"
write("tampered", tampered)
extra = signed(new, "99.0.0", key)
extra["addons/yhde/ui/sneaky.gd"] = b"@tool\nextends Node\n"
write("extra_file", extra)
write("unsigned", new)
write("older", signed(files_with_version("0.0.1"), "0.0.1", key))
write("wrong_key", signed(new, "99.0.0", other))
print("packages ready")
