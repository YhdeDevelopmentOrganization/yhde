@tool
extends EditorPlugin
## Gate test: files that can run code in the editor are held, decided by
## their content, and server addresses follow one rule. Calls the real native functions through YhdeDiagnostics,
## so it needs the native core built with -DYHDE_DIAGNOSTICS=ON. Run through run.sh.

const DIR := "res://gate_files/"

var diag
var fails := 0
var checks := 0


func ok(cond: bool, label: String) -> void:
	checks += 1
	if not cond:
		fails += 1
		print("FAIL ", label)


func _enter_tree() -> void:
	_run.call_deferred()


func _tool_script(base: String) -> GDScript:
	var gd := GDScript.new()
	gd.source_code = "@tool\nextends %s\n" % base
	gd.reload()
	return gd


func _save(res: Resource, file: String, flags := 0) -> String:
	var path := DIR + file
	var err := ResourceSaver.save(res, path, flags)
	ok(err == OK, "save %s (error %d)" % [file, err])
	return path


func _write(file: String, text: String) -> String:
	var path := DIR + file
	var f := FileAccess.open(path, FileAccess.WRITE)
	f.store_string(text)
	f.close()
	return path


func _write_bytes(file: String, bytes: PackedByteArray) -> String:
	var path := DIR + file
	var f := FileAccess.open(path, FileAccess.WRITE)
	f.store_buffer(bytes)
	f.close()
	return path


func _scene(with_tool: bool) -> PackedScene:
	var root := Node.new()
	root.name = "Root"
	if with_tool:
		root.set_script(_tool_script("Node"))
	var packed := PackedScene.new()
	packed.pack(root)
	root.free()
	return packed


func _resource(res: Resource, base: String, with_tool: bool) -> Resource:
	if with_tool:
		res.set_script(_tool_script(base))
	return res


func _expect(path: String, held: bool, label := "") -> void:
	var got: bool = diag.may_run_in_editor(path, path)
	ok(got == held, "%s: held=%s, expected %s" % [label if label != "" else path, got, held])


func _run() -> void:
	if not ClassDB.class_exists("YhdeDiagnostics"):
		print("NO DIAGNOSTICS CLASS")
		get_tree().quit(2)
		return
	diag = ClassDB.instantiate("YhdeDiagnostics")
	DirAccess.make_dir_recursive_absolute(DIR)
	var compress := ResourceSaver.FLAG_COMPRESS

	# Scripts.
	_expect(_write("tool.gd", "@tool\nextends Node\n"), true)
	_expect(_write("plain.gd", "extends Node\n"), false)
	_expect(_write("tokens.gdc", "GDSC\u0001binary"), true, "tokenized GDScript")

	# Scenes and resources with the usual extensions.
	_expect(_save(_scene(true), "t.tscn"), true)
	_expect(_save(_scene(true), "t.scn"), true)
	_expect(_save(_scene(true), "tc.scn", compress), true, "compressed .scn with @tool")
	_expect(_save(_scene(false), "plain.tscn"), false)
	_expect(_save(_scene(false), "plain.scn"), false)
	_expect(_save(_scene(false), "plainc.scn", compress), true, "compressed .scn is always held")
	_expect(_save(_resource(Resource.new(), "Resource", true), "r.tres"), true)
	_expect(_save(_resource(Resource.new(), "Resource", true), "r.res"), true)
	_expect(_save(_resource(Resource.new(), "Resource", true), "rc.res", compress), true, "compressed .res with @tool")
	_expect(_save(_resource(Resource.new(), "Resource", false), "plain.tres"), false)

	# Other resource extensions: decided by content, not name.
	var kinds := [
		["material", StandardMaterial3D.new(), "StandardMaterial3D"],
		["theme", Theme.new(), "Theme"],
		["stylebox", StyleBoxFlat.new(), "StyleBoxFlat"],
		["anim", Animation.new(), "Animation"],
	]
	for k in kinds:
		_expect(_save(_resource(k[1], k[2], true), "x." + k[0]), true, "." + k[0] + " with @tool")
	_expect(_save(StandardMaterial3D.new(), "plain.material"), false, ".material without a script")
	_expect(_save(_resource(Theme.new(), "Theme", true), "xc.theme", compress), true, "compressed .theme")
	_expect(_write("notes.txt", "[gd_resource type=\"Resource\"]\n[ext]\nscript = \"@tool\"\n"), true, "text resource named .txt")
	_expect(_write_bytes("fake.png", "RSCC".to_ascii_buffer() + PackedByteArray([1, 2, 3, 4])), true, ".png that is really a compressed resource")

	var img := Image.create(4, 4, false, Image.FORMAT_RGBA8)
	img.fill(Color.RED)
	img.save_png(DIR + "real.png")
	_expect(DIR + "real.png", false, "a real PNG")
	_expect(_write("readme.md", "Use @tool scripts with care."), false, "text that mentions @tool")
	_expect(_write_bytes("short.res", PackedByteArray([82, 83])), false, ".res shorter than a header")

	# Files that cannot be read: held only if they may be code.
	_expect(DIR + "missing.gd", true, "unreadable .gd")
	_expect(DIR + "missing.png", false, "unreadable .png")

	# C# [Tool] in every spelling the compiler accepts.
	var cs := {
		"[Tool]\npublic partial class A : Node {}": true,
		"[ Tool ]\nclass A {}": true,
		"[Tool()]\nclass A {}": true,
		"[ToolAttribute]\nclass A {}": true,
		"[Godot.Tool]\nclass A {}": true,
		"[global::Godot.Tool]\nclass A {}": true,
		"[GlobalClass, Tool]\nclass A {}": true,
		"[\n\tTool\n]\nclass A {}": true,
		"public class Toolbox {}": false,
		"// shows a ToolTip\nclass A {}": false,
		"class A { int tool = 1; }": false,
		"[Tools]\nclass A {}": false,
	}
	var i := 0
	for text in cs:
		_expect(_write("cs%d.cs" % i, text), cs[text], "C# %s" % text.c_escape())
		i += 1

	# Server addresses: one parser, case-insensitive scheme,
	# all of 127.0.0.0/8 is this computer, user names never make it loopback.
	var urls := [
		["ws://localhost/ws", true, true, "localhost"],
		["WS://LOCALHOST:9/ws", true, true, "localhost"],
		["ws://127.0.0.1:8080/ws", true, true, "127.0.0.1"],
		["ws://127.0.0.2/ws", true, true, "127.0.0.2"],
		["ws://127.255.255.254/ws", true, true, "127.255.255.254"],
		["ws://[::1]:8080/ws", true, true, "[::1]"],
		["wss://example.com:443/ws?x=1#y", true, false, "example.com"],
		["ws://example.com/ws", true, false, "example.com"],
		["ws://localhost.evil.com/ws", true, false, "localhost.evil.com"],
		["ws://127.evil.com/ws", true, false, "127.evil.com"],
		["ws://127.0.0.256/ws", true, false, "127.0.0.256"],
		["ws://127.1/ws", true, false, "127.1"],
		["ws://user@127.0.0.1/ws", true, false, "127.0.0.1"],
		["ws://127.0.0.1@evil.com/ws", true, false, "evil.com"],
		["ws://a@b@evil.com/ws", true, false, "evil.com"],
		["ws:///ws", false, false, ""],
		["ws://host:abc/ws", false, false, ""],
		["ws://[::1/ws", false, false, ""],
		["localhost:8080", false, false, ""],
		["", false, false, ""],
	]
	for u in urls:
		var p: Dictionary = diag.parse_url(u[0])
		ok(p.ok == u[1], "parse_url(%s).ok = %s, expected %s" % [u[0], p.ok, u[1]])
		ok(p.loopback == u[2], "parse_url(%s).loopback = %s, expected %s" % [u[0], p.loopback, u[2]])
		if u[1]:
			ok(p.host == u[3], "parse_url(%s).host = %s, expected %s" % [u[0], p.host, u[3]])

	# Shared paths: the same table the server's tests read.
	var table = JSON.parse_string(FileAccess.get_file_as_string("res://path_rules.json"))
	if table == null:
		ok(false, "path_rules.json could not be read")
	else:
		for path in table.paths:
			var reason: String = diag.exclusion_reason(path)
			ok((reason == "") == bool(table.paths[path]), "path %s: shared=%s, expected %s (%s)" % [path.c_escape(), reason == "", table.paths[path], reason])

	print("gate: checks=%d fails=%d" % [checks, fails])
	get_tree().quit(1 if fails > 0 else 0)
