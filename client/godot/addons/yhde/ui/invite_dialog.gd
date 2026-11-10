@tool
extends AcceptDialog
## Invite people into one project: an invite link that lets them edit or only
## watch, copied to the clipboard. The project's short code and an email
## invitation are there too, smaller. Used by the first-run view and the
## People tab. Works for the project's owner; the server refuses anyone else.

const Style := preload("style.gd")

var account: Node # ui/account.gd
var project_id := ""
var project_name := ""

var _edit: CheckBox
var _watch: CheckBox
var _copy: Button
var _status: Label
var _code_button: Button
var _code: Label
var _email: LineEdit
var _send: Button


func _init() -> void:
	ok_button_text = "Done"
	dialog_hide_on_ok = true
	exclusive = true
	min_size = Vector2i(int(Style.px(380)), 0)
	confirmed.connect(queue_free)
	canceled.connect(queue_free)


## Opens it over the editor for this project.
func open(acc: Node, id: String, name: String) -> void:
	account = acc
	project_id = id
	project_name = name
	title = "Invite people to %s" % name
	_build()
	EditorInterface.get_base_control().add_child(self)
	popup_centered()


func _build() -> void:
	var col := VBoxContainer.new()
	col.add_theme_constant_override("separation", int(Style.px(8)))
	var group := ButtonGroup.new()
	_edit = CheckBox.new()
	_edit.text = "Can edit"
	_edit.button_group = group
	_edit.button_pressed = true
	col.add_child(_edit)
	_watch = CheckBox.new()
	_watch.text = "Can only watch"
	_watch.button_group = group
	col.add_child(_watch)
	group.pressed.connect(func(_b: BaseButton) -> void:
		_say("")
		_code_button.visible = _edit.button_pressed and _code.text == ""
		_code.visible = _edit.button_pressed and _code.text != "")

	_copy = Button.new()
	_copy.text = "Copy invite link"
	Style.primary_button(_copy)
	_copy.pressed.connect(_copy_link)
	col.add_child(_copy)
	_status = Style.label("", true, Style.small_size())
	_status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_status.visible = false
	col.add_child(_status)

	col.add_child(Style.hairline())
	_code_button = Button.new()
	_code_button.text = "Or give them a code"
	_code_button.alignment = HORIZONTAL_ALIGNMENT_LEFT
	Style.quiet_button(_code_button)
	_code_button.pressed.connect(_show_code)
	col.add_child(_code_button)
	_code = Style.label("", true, Style.small_size())
	_code.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_code.visible = false
	col.add_child(_code)

	var mail := HBoxContainer.new()
	mail.add_theme_constant_override("separation", int(Style.px(6)))
	_email = LineEdit.new()
	_email.placeholder_text = "Or their email"
	_email.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	Style.field(_email)
	_email.text_submitted.connect(func(_t: String) -> void: _send_email())
	mail.add_child(_email)
	_send = Button.new()
	_send.text = "Send"
	Style.secondary_button(_send)
	_send.pressed.connect(_send_email)
	mail.add_child(_send)
	col.add_child(mail)
	add_child(col)


func _copy_link() -> void:
	var access := "edit" if _edit.button_pressed else "view"
	_copy.disabled = true
	_say("")
	var url: String = await account.make_link(project_id, access)
	_copy.disabled = false
	if url == "":
		return # the account's problem signal says why
	DisplayServer.clipboard_set(url)
	_say("Copied. Send it to one person. It works for 7 days.")


func _show_code() -> void:
	_code_button.disabled = true
	var code: String = await account.join_code(project_id)
	_code_button.disabled = false
	if code == "":
		return
	_code.text = "Code: %s\nThey type it under Have a code? on the YHDE website, after signing in." % code
	_code.visible = true
	_code_button.visible = false


func _send_email() -> void:
	var email := _email.text.strip_edges()
	if email == "":
		return
	_send.disabled = true
	var ok: bool = await account.invite(project_id, email)
	_send.disabled = false
	if ok:
		_email.text = ""
		_say("Invitation sent to %s." % email)


## The line under Copy invite link; hidden while it has nothing to say.
func _say(text: String) -> void:
	_status.text = text
	_status.visible = text != ""
