@tool
extends RefCounted
## Where everyone is, shown in the editor itself: small avatars on the scene
## tabs, next to the nodes people have selected in the Scene tree, next to the
## scenes and scripts they have open in the FileSystem dock, and in the Script
## editor's file list (with "typing"). Each place can be turned off in the
## dock's options.
##
## These editor parts are internal (EditorSceneTabs, SceneTreeEditor, the
## script list); each is looked up by class and skipped if it is not there.

const Style := preload("style.gd")
const Layer := preload("mark_layer.gd")

var session: Object
var options := {}
var _layers := {} # place -> mark_layer
var _peers: Array = []
# Editor parts found once and remembered: place -> [instance id, when to look
# again if it is gone]. Searching the whole editor UI (tens of thousands of
# controls) four times a second made the editor stutter, even offline.
var _found := {}

const SEARCH_AGAIN_MS := 2000


func refresh(peers: Array) -> void:
	_peers = peers
	# Nothing to show (offline, or nobody else here): no layers, no lookups.
	if session == null or session.get_state() != Style.STATE_LIVE or peers.is_empty():
		clear()
		return
	_place("scene_tabs", _scene_tab_bar, _scene_tab_marks)
	_place("scene_tree", _scene_tree, _scene_tree_marks)
	_place("filesystem", _filesystem_tree, _filesystem_marks, _visible)
	_place("script_list", _script_list, _script_list_marks)


func clear() -> void:
	for layer in _layers.values():
		if is_instance_valid(layer):
			layer.queue_free()
	_layers.clear()


## The editor part `find` returns, remembered between calls. When it has gone
## (a dock closed or rebuilt) it is looked for again, at most every 2 s.
func _cached(key: String, find: Callable, still_ok := Callable()) -> Control:
	var entry = _found.get(key)
	var now := Time.get_ticks_msec()
	if entry != null:
		var c = instance_from_id(entry[0]) if entry[0] != 0 else null
		if c is Control and is_instance_valid(c) and c.is_inside_tree() and (not still_ok.is_valid() or still_ok.call(c)):
			return c
		if now < entry[1]:
			return null
	var found = find.call()
	_found[key] = [found.get_instance_id() if found != null else 0, now + SEARCH_AGAIN_MS]
	return found


func _place(place: String, find: Callable, provider: Callable, still_ok := Callable()) -> void:
	var layer = _layers.get(place)
	var target: Control = _cached(place, find, still_ok) if options.get(place, true) else null
	var wanted: bool = target != null
	if not wanted:
		if layer and is_instance_valid(layer):
			layer.queue_free()
		_layers.erase(place)
		return
	if layer == null or not is_instance_valid(layer) or layer.get_parent() != target:
		if layer and is_instance_valid(layer):
			layer.queue_free()
		layer = Layer.new()
		layer.provider = provider
		target.add_child(layer, false, Node.INTERNAL_MODE_BACK)
		_layers[place] = layer
	layer.queue_redraw()


static func _person(p: Dictionary) -> Dictionary:
	return {"name": p.name, "color": p.color, "initials": p.initials, "typing": p.get("typing", false)}


# Scene tabs

static func _visible(c: Control) -> bool:
	return c.is_visible_in_tree()


func _scene_tab_bar() -> TabBar:
	var tabs := EditorInterface.get_base_control().find_children("*", "EditorSceneTabs", true, false)
	if tabs.is_empty():
		return null
	var bars: Array = tabs[0].find_children("*", "TabBar", true, false)
	return bars[0] if not bars.is_empty() else null


func _scene_tab_marks(bar: Control) -> Array:
	var tab_bar := bar as TabBar
	var scenes := EditorInterface.get_open_scenes()
	var out: Array = []
	for i in mini(tab_bar.tab_count, scenes.size()):
		var people: Array = []
		for p in _peers:
			if p.scene != "" and p.scene == scenes[i]:
				people.append(_person(p))
		if not people.is_empty():
			var r := tab_bar.get_tab_rect(i)
			out.append({"rect": Rect2(r.position, Vector2(r.size.x - Style.px(4), Style.px(12))), "people": people, "small": true})
	return out


# Scene tree (nodes others have selected)

func _scene_tree() -> Tree:
	var docks := EditorInterface.get_base_control().find_children("*", "SceneTreeDock", true, false)
	if docks.is_empty():
		return null
	for t in docks[0].find_children("*", "Tree", true, false):
		if t.get_parent() and t.get_parent().get_class() == "SceneTreeEditor":
			return t
	return null


func _scene_tree_marks(target: Control) -> Array:
	var tree := target as Tree
	var root := EditorInterface.get_edited_scene_root()
	if root == null or tree.get_root() == null:
		return []
	var scene := root.scene_file_path
	var by_path := {}
	for p in _peers:
		if p.scene != scene:
			continue
		for id in p.get("selection_ids", []):
			var node = session.find_node_by_id(scene, str(id))
			if node is Node and is_instance_valid(node):
				var key := str(node.get_path())
				if not by_path.has(key):
					by_path[key] = []
				by_path[key].append(_person(p))
	if by_path.is_empty():
		return []
	var out: Array = []
	var item := tree.get_root()
	while item:
		var meta = item.get_metadata(0)
		if meta != null and by_path.has(str(meta)):
			var rect := tree.get_item_area_rect(item, 0)
			if item.get_button_count(0) > 0:
				# Stop before the row's buttons (visibility, script, etc.).
				rect.size.x = tree.get_item_area_rect(item, 0, 0).position.x - rect.position.x
			out.append({"rect": rect, "people": by_path[str(meta)]})
		item = item.get_next_visible()
	return out


# FileSystem dock (scenes and scripts others have open)

func _filesystem_tree() -> Tree:
	var fs := EditorInterface.get_file_system_dock()
	if fs == null:
		return null
	for t in fs.find_children("*", "Tree", true, false):
		if t.is_visible_in_tree():
			return t
	return null


func _filesystem_marks(target: Control) -> Array:
	var tree := target as Tree
	var by_path := {}
	for p in _peers:
		for key in [p.scene, p.get("script", "")]:
			if key == "":
				continue
			if not by_path.has(key):
				by_path[key] = []
			by_path[key].append(_person(p))
	if by_path.is_empty() or tree.get_root() == null:
		return []
	var out: Array = []
	var item := tree.get_root()
	while item:
		var meta = item.get_metadata(0)
		if meta is String and by_path.has(meta):
			out.append({"rect": tree.get_item_area_rect(item, 0), "people": by_path[meta]})
		item = item.get_next_visible()
	return out


# Script editor's file list

func script_list() -> ItemList:
	return _cached("script_list_any", _script_list) as ItemList


func _script_list() -> ItemList:
	var se := EditorInterface.get_script_editor()
	if se == null:
		return null
	for list in se.find_children("*", "ItemList", true, false):
		if list.item_count > 0 and str(list.get_item_tooltip(0)).begins_with("res://"):
			return list
	return null


func _script_list_marks(target: Control) -> Array:
	var list := target as ItemList
	var out: Array = []
	for i in list.item_count:
		var path := str(list.get_item_tooltip(i))
		var people: Array = []
		for p in _peers:
			if p.get("script", "") == path:
				people.append(_person(p))
		if not people.is_empty():
			out.append({"rect": list.get_item_rect(i), "people": people})
	return out
