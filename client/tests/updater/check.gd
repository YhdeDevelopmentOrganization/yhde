extends SceneTree
## Feeds every test package to Updater.check_release(): only "good" may pass.

const Updater := preload("res://addons/yhde/ui/updater.gd")
const EXPECT := {"good": true, "tampered": false, "extra_file": false, "unsigned": false, "older": false, "wrong_key": false}


func _init() -> void:
	var fails := 0
	for name in EXPECT:
		var zip := ZIPReader.new()
		if zip.open("res://packages/%s.zip" % name) != OK:
			print("FAIL %s: cannot open" % name)
			fails += 1
			continue
		var files := {}
		for f in zip.get_files():
			files[f] = zip.read_file(f)
		zip.close()
		var problem: String = Updater.check_release(files)
		var passed := problem == ""
		if passed == EXPECT[name]:
			print("ok   %s: %s" % [name, "accepted" if passed else problem])
		else:
			print("FAIL %s: %s" % [name, "accepted" if passed else problem])
			fails += 1
	print("updater: fails=%d" % fails)
	quit(1 if fails > 0 else 0)
