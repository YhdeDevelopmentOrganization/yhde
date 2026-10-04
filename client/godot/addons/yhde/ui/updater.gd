@tool
extends Node
## Add-on updates from the server: the server hands out the
## add-on its admin uploaded. When it is newer than this one, the dock offers
## it; installing downloads it, checks its SHA-256 against the server's
## manifest and its release signature against RELEASE_KEYS, writes it over
## addons/yhde and asks for a restart. (Godot loads a copy of the native core,
## "~libyhde*", so the file itself can be replaced while the editor runs; the
## new one is loaded on restart.)
##
## The checksum comes from the same server as the package, so it only catches
## damage. The signature is what makes an update YHDE's: any server can hand
## out releases, none can make one (security.md, "Add-on and Server Updates").

signal available(version: String)
## state: "downloading", "installed", "failed"
signal progress(state: String, text: String)

const ADDON := "res://addons/yhde/"
const SIG := "addons/yhde/release.sig"
## Public keys whose signatures make an add-on package a release
## (client/package_addon.py --sign). A build of your own (a fork) puts its own
## key here.
const RELEASE_KEYS: Array[String] = [
	"""-----BEGIN PUBLIC KEY-----
MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAwE6QEiTCKd3JuhRP4cZ/
z3DCXY2Rv/94PWDkHbqc97G/9xKw9/hJDx6Q6j7LLybEu0lu5CYUpowa6rUiSILV
RDzwiCsyKCJIvE0xcaLsMAenUNXq6HLkG7bEMeVepz+jLpX6kK9mBVqsFaQJw01j
lguMS/agGL1l70kGNQNQQOhXihn7J1J1ekoRQreNcpr9mF2/ArJkSYbOD7vl0xUm
ebv3xnav83JzcvsJDIFjq0QzfuIvaKj84fvcyJSpog0yIhmpkp02LwnESYeRW4uO
+yznLGZHo+m8NErpUMhq++QLfmNu27aPx5a9/0AoIIOJZORpCW54Be7pgaObudJq
INgOAQeIyk7XZsMpHsOOo2JlCYuqedp8EOMkKTJSwQ/xSj6Ht1knpn/G+jq+/91Y
VnuuDkFpDvcV/zOvFmUVpZaDX2VZ5uVsF+A8yBovizfDvB5JQcRX8Xnia+mqvR5Y
ive3ykb0S3f/VRxLvuBdO/pxWb9cW8fxE6efDVMryMlIyjwCDZsDzLCzB3njlpEn
pEOw8jEFykKSObMuPPZuVxo92ID7unnOG88nc9uf9D87U9WrhwETIMKTjp0RzW4z
YkCh01Nt7Pp/hLeXqEzdBHT5l9lcZ7Vf/GguCUVlcbBfMfd8rHXhs0UbXHzshLOS
wziGae4GJeRclNdukqIXLp8CAwEAAQ==
-----END PUBLIC KEY-----
""",
]

var _base := ""   # https://host of the server
var _latest := {} # the server's manifest
var _http: HTTPRequest
var _busy := false


func _ready() -> void:
	_http = HTTPRequest.new()
	_http.timeout = 60.0
	_http.max_redirects = 2
	add_child(_http)


static func installed_version() -> String:
	var cfg := ConfigFile.new()
	if cfg.load(ADDON + "plugin.cfg") != OK:
		return "0"
	return str(cfg.get_value("plugin", "version", "0"))


## True when version a is newer than b ("0.10.0" > "0.9.2").
static func newer(a: String, b: String) -> bool:
	var pa := a.split(".")
	var pb := b.split(".")
	for i in maxi(pa.size(), pb.size()):
		var x := int(pa[i]) if i < pa.size() else 0
		var y := int(pb[i]) if i < pb.size() else 0
		if x != y:
			return x > y
	return false


## Asks the server behind a ws(s):// address whether it has a newer add-on.
func check(server_url: String) -> void:
	if _busy:
		return
	_base = server_url.replace("wss://", "https://").replace("ws://", "http://").trim_suffix("/").trim_suffix("/ws")
	_busy = true
	var err := _http.request(_base + "/addon/manifest.json")
	if err != OK:
		_busy = false
		return
	var result: Array = await _http.request_completed
	_busy = false
	if result[0] != HTTPRequest.RESULT_SUCCESS or result[1] != 200:
		return # an older server, or no add-on uploaded: nothing to offer
	var m = JSON.parse_string((result[3] as PackedByteArray).get_string_from_utf8())
	if not (m is Dictionary) or not m.has("version") or not m.has("sha256"):
		return
	_latest = m
	if newer(str(m.version), installed_version()) and _has_my_platform(m):
		available.emit(str(m.version))


func _has_my_platform(m: Dictionary) -> bool:
	var mine := {"Windows": "windows", "Linux": "linux", "macOS": "macos"}.get(OS.get_name(), "")
	return mine == "" or (m.get("platforms", []) as Array).has(mine)


func install() -> void:
	if _busy or _latest.is_empty():
		return
	_busy = true
	progress.emit("downloading", "Downloading YHDE %s…" % _latest.version)
	_http.download_file = ""
	var err := _http.request(_base + "/addon/yhde-addon.zip")
	if err != OK:
		_fail("Could not start the download (%s)." % error_string(err))
		return
	var result: Array = await _http.request_completed
	if result[0] != HTTPRequest.RESULT_SUCCESS or result[1] != 200:
		_fail("The download failed (%s)." % ("HTTP %d" % result[1] if result[0] == HTTPRequest.RESULT_SUCCESS else "network"))
		return
	var bytes: PackedByteArray = result[3]
	var ctx := HashingContext.new()
	ctx.start(HashingContext.HASH_SHA256)
	ctx.update(bytes)
	if ctx.finish().hex_encode() != str(_latest.sha256):
		_fail("The download was damaged (checksum mismatch). Nothing was changed; try again.")
		return
	var problem := _unpack(bytes)
	if problem != "":
		_fail(problem)
		return
	_busy = false
	progress.emit("installed", "YHDE %s is installed. Restart Godot to use it." % _latest.version)


## Writes the package over addons/yhde. Everything is read and checked first,
## so a bad package changes nothing.
func _unpack(bytes: PackedByteArray) -> String:
	var tmp := OS.get_user_data_dir().path_join("yhde-update.zip")
	var f := FileAccess.open(tmp, FileAccess.WRITE)
	if f == null:
		return "Could not save the download."
	f.store_buffer(bytes)
	f.close()
	var zip := ZIPReader.new()
	if zip.open(tmp) != OK:
		return "The download is not a zip."
	var files := {}
	for name in zip.get_files():
		if name.ends_with("/"):
			continue
		if not name.begins_with("addons/yhde/") or name.contains("..") or name.contains(":"):
			zip.close()
			return "The update contains an unexpected file (%s). Nothing was changed." % name
		files[name] = zip.read_file(name)
	zip.close()
	DirAccess.remove_absolute(tmp)
	if not files.has("addons/yhde/plugin.cfg"):
		return "The update is incomplete. Nothing was changed."
	var problem := check_release(files)
	if problem != "":
		return problem
	for name in files:
		var path: String = "res://" + name
		DirAccess.make_dir_recursive_absolute(path.get_base_dir())
		var out := FileAccess.open(path, FileAccess.WRITE)
		if out == null:
			return "Could not write %s (%s). Close other programs using the project and try again." % [path, error_string(FileAccess.get_open_error())]
		out.store_buffer(files[name])
		out.close()
	return ""


## Is this package a signed YHDE release newer than the one installed? ""
## when it is, otherwise why not. `files`: zip path -> bytes.
static func check_release(files: Dictionary) -> String:
	if not files.has(SIG):
		return "This update is not signed as a YHDE release, so it was not installed. Nothing was changed."
	var cfg := ConfigFile.new()
	if cfg.parse((files["addons/yhde/plugin.cfg"] as PackedByteArray).get_string_from_utf8()) != OK:
		return "The update is damaged (plugin.cfg). Nothing was changed."
	var version := str(cfg.get_value("plugin", "version", ""))
	# A real but older release (with a fixed bug) is no update.
	if version == "" or not newer(version, installed_version()):
		return "The update (%s) is not newer than this add-on (%s). Nothing was changed." % [version, installed_version()]
	var text := release_text(version, files)
	var ctx := HashingContext.new()
	ctx.start(HashingContext.HASH_SHA256)
	ctx.update(text.to_utf8_buffer())
	var hash := ctx.finish()
	var signature := Marshalls.base64_to_raw((files[SIG] as PackedByteArray).get_string_from_utf8().strip_edges())
	var crypto := Crypto.new()
	for pem in RELEASE_KEYS:
		var key := CryptoKey.new()
		if key.load_from_string(pem, true) == OK and crypto.verify(HashingContext.HASH_SHA256, hash, signature, key):
			return ""
	return "This update's signature does not match YHDE's release key, so it was not installed. Nothing was changed."


## The text a release signature covers; package_addon.py's digest() makes the
## same: the version and every file's SHA-256, by path.
static func release_text(version: String, files: Dictionary) -> String:
	var names: Array = files.keys().filter(func(n: String) -> bool: return n != SIG)
	names.sort()
	var text := "yhde-addon-release 1\nversion %s\n" % version
	for name in names:
		var ctx := HashingContext.new()
		ctx.start(HashingContext.HASH_SHA256)
		ctx.update(files[name])
		text += "%s  %s\n" % [ctx.finish().hex_encode(), name]
	return text


func _fail(text: String) -> void:
	_busy = false
	progress.emit("failed", text)
