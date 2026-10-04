@tool
extends RefCounted
## Chat and comments as this editor knows them. The server is the
## source of truth; this keeps what it sent, answers the UI's questions and
## remembers per person what has been read.

signal chat_changed(conversation: String)
signal comments_changed
signal unread_changed
signal error(message: String)
## A message addressed to you (direct, or an @mention) arrived.
signal pinged(text: String, kind: String) # kind: dm, mention, comment

const PROJECT := "project" # the project channel's conversation key
const META := "yhde"

var session: Object
var me := ""        # my member id
var my_name := ""

## conversation key ("project" or a member id) -> Array of message dictionaries
var _messages: Dictionary = {}
## member id -> last known display name
var _names: Dictionary = {}
## thread id -> thread dictionary
var threads: Dictionary = {}
var _more_history: Dictionary = {}


func reset() -> void:
	_messages.clear()
	threads.clear()
	_more_history.clear()
	chat_changed.emit(PROJECT)
	comments_changed.emit()
	unread_changed.emit()


# From the server

func handle(kind: String, body: Dictionary, _request_id: String) -> void:
	match kind:
		"chat.recent":
			for m in body.get("messages", []):
				_add_message(m, false)
			_more_history[PROJECT] = body.get("more", false)
			chat_changed.emit(PROJECT)
			unread_changed.emit()
		"chat.message":
			var key := _add_message(body, true)
			chat_changed.emit(key)
			unread_changed.emit()
		"chat.history":
			var key := PROJECT if body.get("with") == null else str(body.with)
			for m in body.get("messages", []):
				_add_message(m, false)
			_more_history[key] = body.get("more", false)
			chat_changed.emit(key)
		"comment.threads":
			threads.clear()
			for t in body.get("threads", []):
				threads[str(t.id)] = t
			comments_changed.emit()
			unread_changed.emit()
		"comment.thread":
			var id := str(body.id)
			var before: Dictionary = threads.get(id, {})
			threads[id] = body
			if _mentions_me_newly(before, body):
				pinged.emit("%s mentioned you in a comment" % _last_author(body), "comment")
			comments_changed.emit()
			unread_changed.emit()
		"error":
			error.emit(str(body.get("message", "Something went wrong.")))


func _add_message(m: Dictionary, live: bool) -> String:
	var from: Dictionary = m.get("from", {})
	var from_id := str(from.get("id", ""))
	_names[from_id] = str(from.get("name", "?"))
	var to = m.get("to")
	var key := PROJECT
	if to != null:
		key = str(to) if from_id == me else from_id
	var list: Array = _messages.get(key, [])
	for existing in list:
		if existing.id == m.id:
			return key
	list.append(m)
	list.sort_custom(func(a, b) -> bool: return float(a.at) < float(b.at))
	_messages[key] = list
	if live and from_id != me:
		if key != PROJECT:
			pinged.emit("%s: %s" % [_names[from_id], _preview(str(m.body))], "dm")
		elif mentions_me(str(m.body)):
			pinged.emit("%s mentioned you: %s" % [_names[from_id], _preview(str(m.body))], "mention")
	return key


# Questions from the UI

func messages(conversation: String) -> Array:
	return _messages.get(conversation, [])


func has_more_history(conversation: String) -> bool:
	return _more_history.get(conversation, conversation != PROJECT)


## Direct-message partners, most recent first: [{id, name, last}]
func conversations() -> Array:
	var out: Array = []
	for key in _messages:
		if key == PROJECT:
			continue
		var list: Array = _messages[key]
		out.append({"id": key, "name": name_of(key), "last": float(list[-1].at) if not list.is_empty() else 0.0})
	out.sort_custom(func(a, b) -> bool: return a.last > b.last)
	return out


func name_of(member: String) -> String:
	return _names.get(member, "Someone")


## A person's color and initials, the same as on their cursor and avatar.
func color_of(name: String) -> Color:
	return session.get_color_for(name) if session else Color.GRAY


func initials_of(name: String) -> String:
	return session.get_initials_for(name) if session else "?"


func remember_name(member: String, name: String) -> void:
	if member != "" and name != "":
		_names[member] = name


func unread(conversation: String) -> int:
	var seen := _read_mark("chat/" + conversation)
	var n := 0
	for m in messages(conversation):
		if float(m.at) > seen and str(m.from.id) != me:
			n += 1
	return n


func unread_chat_total() -> int:
	var n := unread(PROJECT)
	for c in conversations():
		n += unread(c.id)
	return n


func mark_read(conversation: String) -> void:
	var list := messages(conversation)
	if list.is_empty():
		return
	_set_read_mark("chat/" + conversation, float(list[-1].at))
	unread_changed.emit()


## Threads for a scene (or every scene with ""), open first, newest last.
func thread_list(scene: String, include_resolved: bool) -> Array:
	var out: Array = []
	for t in threads.values():
		if scene != "" and t.scene != scene:
			continue
		if t.get("resolved") != null and not include_resolved:
			continue
		if (t.messages as Array).is_empty():
			continue
		out.append(t)
	out.sort_custom(func(a, b) -> bool: return float(a.created) < float(b.created))
	return out


## Open threads with activity from others since you last looked.
func unread_comments() -> int:
	var seen := _read_mark("comments")
	var n := 0
	for t in threads.values():
		if t.get("resolved") != null:
			continue
		for m in t.messages:
			if float(m.at) > seen and str(m.author.id) != me:
				n += 1
				break
	return n


func mark_comments_read() -> void:
	_set_read_mark("comments", Time.get_unix_time_from_system() * 1000.0)
	unread_changed.emit()


func number_of(thread_id: String) -> int:
	# Figma-style pin numbers: the order threads were made in.
	var ids: Array = []
	for t in threads.values():
		ids.append([float(t.created), str(t.id)])
	ids.sort()
	for i in ids.size():
		if ids[i][1] == thread_id:
			return i + 1
	return 0


func mentions_me(text: String) -> bool:
	if my_name == "":
		return false
	var lower := text.to_lower()
	return lower.contains("@" + my_name.to_lower()) or lower.contains("@everyone") or lower.contains("@here")


# Requests

func send_chat(conversation: String, text: String) -> bool:
	var body := {"body": text}
	if conversation != PROJECT:
		body["to"] = conversation
	return _send("chat.send", body)


func load_older(conversation: String) -> void:
	var list := messages(conversation)
	var body := {"with": null if conversation == PROJECT else conversation}
	if not list.is_empty():
		body["before"] = list[0].at
	_send("chat.history", body)


func create_comment(scene: String, node_id: String, node_path: String, anchor: Dictionary, text: String) -> bool:
	return _send("comment.create", {
		"scene": scene,
		"node": node_id if node_id != "" else null,
		"path": node_path,
		"anchor": anchor,
		"body": text,
	})


func reply(thread_id: String, text: String) -> bool:
	return _send("comment.reply", {"thread": thread_id, "body": text})


func set_resolved(thread_id: String, resolved: bool) -> bool:
	return _send("comment.resolve", {"thread": thread_id, "resolved": resolved})


func edit_comment(message_id: String, text: String) -> bool:
	return _send("comment.edit", {"message": message_id, "body": text})


func delete_comment(message_id: String) -> bool:
	return _send("comment.delete", {"message": message_id})


func _send(kind: String, body: Dictionary) -> bool:
	if session == null or not session.has_social():
		error.emit("Chat and comments need a connection to a YHDE 0.2 (or newer) server.")
		return false
	if session.social_send(kind, body) == "":
		error.emit("Not connected: your message was not sent.")
		return false
	return true


# Helpers

func _read_mark(key: String) -> float:
	return float(EditorInterface.get_editor_settings().get_project_metadata(META, "read/" + key, 0.0))


func _set_read_mark(key: String, at: float) -> void:
	if at > _read_mark(key):
		EditorInterface.get_editor_settings().set_project_metadata(META, "read/" + key, at)


func _mentions_me_newly(before: Dictionary, after: Dictionary) -> bool:
	var old_ids := {}
	for m in before.get("messages", []):
		old_ids[m.id] = true
	for m in after.get("messages", []):
		if not old_ids.has(m.id) and str(m.author.id) != me and mentions_me(str(m.body)):
			return true
	return false


func _last_author(t: Dictionary) -> String:
	var list: Array = t.get("messages", [])
	return str(list[-1].author.name) if not list.is_empty() else "Someone"


static func _preview(text: String) -> String:
	var one := text.replace("\n", " ")
	return one if one.length() <= 80 else one.substr(0, 77) + "..."
