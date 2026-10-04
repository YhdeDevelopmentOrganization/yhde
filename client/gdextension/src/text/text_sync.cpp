#include "text/text_sync.h"

#include "sync/variant_codec.h"

#include <godot_cpp/classes/dir_access.hpp>
#include <godot_cpp/classes/file_access.hpp>
#include <godot_cpp/classes/input_event_key.hpp>
#include <godot_cpp/classes/resource_loader.hpp>
#include <godot_cpp/classes/resource_saver.hpp>
#include <godot_cpp/classes/script.hpp>
#include <godot_cpp/classes/shader.hpp>
#include <godot_cpp/classes/shader_include.hpp>
#include <godot_cpp/core/object.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

using namespace godot;

namespace yhde {

namespace {

constexpr double kUndoGroupSeconds = 1.0; // typing within this counts as one undo step
constexpr double kWriteDelay = 0.3;       // closed files are written this long after the last change
constexpr size_t kMaxUndo = 200;

String read_text(const String &path) {
	if (!FileAccess::file_exists(path)) return String();
	return normalize_text(FileAccess::get_file_as_string(path));
}

} // namespace

void TextSync::configure(const String &state_dir) {
	state_dir_ = state_dir;
	reset();
}

void TextSync::reset() {
	unbind_all();
	docs_.clear();
}

bool TextSync::is_text_path(const String &path) {
	static const char *kExts[] = { "gd", "gdshader", "gdshaderinc", "cs", "json", "txt", "md", "cfg", "ini", "yml",
		"yaml", "xml", "html", "htm", "js", "css", "py", "lua", "glsl", "hlsl", "toml" };
	String ext = path.get_extension().to_lower();
	for (const char *e : kExts) {
		if (ext == e) return true;
	}
	return false;
}

bool TextSync::is_shader_path(const String &path) {
	String ext = path.get_extension().to_lower();
	return ext == "gdshader" || ext == "gdshaderinc";
}

// Puts text into the cached Script / Shader / ShaderInclude of a file, so
// open editors and running tools use it. Returns the resource, or null.
static Ref<Resource> set_cached_text(const String &path, const String &text) {
	ResourceLoader *loader = ResourceLoader::get_singleton();
	if (!loader->has_cached(path)) return Ref<Resource>();
	Ref<Resource> res = loader->get_cached_ref(path);
	if (Ref<Script> script = res; script.is_valid()) {
		script->set_source_code(text);
	} else if (Ref<Shader> shader = res; shader.is_valid()) {
		shader->set_code(text);
	} else if (Ref<ShaderInclude> inc = res; inc.is_valid()) {
		inc->set_code(text);
	} else {
		return Ref<Resource>();
	}
	return res;
}

String TextSync::state_path(const String &path) const {
	return state_dir_.path_join(path.md5_text() + ".json");
}

// Documents

TextSync::Doc *TextSync::doc_for(const String &path, bool create) {
	auto it = docs_.find(path);
	if (it != docs_.end()) return &it->second;
	if (!create || !is_text_path(path)) return nullptr;
	Doc d;
	d.path = path;
	if (!load_doc(d)) return nullptr;
	Doc &stored = (docs_[path] = d);
	send_next(stored); // typed while offline
	return &stored;
}

bool TextSync::load_doc(Doc &d) {
	int64_t asset_seq = 0;
	bool shared = hooks_.shared && hooks_.shared(d.path, &asset_seq);
	// What this editor last knew of the file's live text.
	String state = state_path(d.path);
	if (!state_dir_.is_empty() && FileAccess::file_exists(state)) {
		Variant v;
		if (from_json(FileAccess::get_file_as_string(state), v) && v.get_type() == Variant::DICTIONARY) {
			Dictionary s = v;
			int64_t rev = int64_t(double(s.get("rev", 0.0)));
			if (String(s.get("path", String())) == d.path && rev >= asset_seq && asset_seq > 0) {
				d.rev = rev;
				d.server_text = s.get("text", String());
				// Typed while offline (or saved outside the editor): sent as edits.
				d.text = FileAccess::file_exists(d.path) ? read_text(d.path) : d.server_text;
				d.dirty = false;
				if (d.text != d.server_text) {
					d.buffer = TextOperation::diff(d.server_text, d.text);
					d.has_buffer = !d.buffer.is_noop();
				}
				return true;
			}
		}
	}
	if (!shared) return false;
	d.rev = asset_seq;
	d.server_text = read_text(d.path);
	d.text = d.server_text;
	return true;
}

bool TextSync::is_bound(const String &path) const {
	auto it = docs_.find(path);
	return it != docs_.end() && it->second.editor != 0;
}

CodeEdit *TextSync::editor_of(const Doc &d) const {
	if (d.editor == 0) return nullptr;
	return Object::cast_to<CodeEdit>(ObjectDB::get_instance(d.editor));
}

// Binding to the script editor

void TextSync::bind(CodeEdit *editor, const String &path) {
	if (!editor || !is_text_path(path)) return;
	uint64_t id = editor->get_instance_id();
	auto known = editors_.find(id);
	if (known != editors_.end()) {
		if (known->second == path) return;
		unbind(editor);
	}
	Doc *d = doc_for(path, true);
	if (!d) return; // not a shared file (yet): the file itself is shared on save
	if (d->editor != 0 && d->editor != id) return; // already bound to another tab
	d->editor = id;
	editors_[id] = path;
	editor->connect("text_changed", callable_mp_static(&TextSync::route_text_changed).bind(uint64_t(reinterpret_cast<uintptr_t>(this)), id));
	editor->connect("gui_input", callable_mp_static(&TextSync::route_gui_input).bind(uint64_t(reinterpret_cast<uintptr_t>(this)), id));
	// Whatever this tab shows beyond the shared text (typed before the file
	// was shared, or before connecting) goes out as an edit.
	String shown = normalize_text(editor->get_text());
	if (shown != d->text) {
		// The tab is older than what the log says (someone typed meanwhile):
		// the log wins where the tab was untouched.
		if (editor->get_version() == editor->get_saved_version()) {
			applying_ = true;
			editor->set_text(d->text);
			editor->tag_saved_version();
			editor->clear_undo_history();
			applying_ = false;
		} else {
			local_change(*d, TextOperation::diff(d->text, shown), false);
		}
	}
}

void TextSync::unbind(CodeEdit *editor) {
	if (!editor) return;
	uint64_t id = editor->get_instance_id();
	auto it = editors_.find(id);
	if (it == editors_.end()) return;
	Callable changed = callable_mp_static(&TextSync::route_text_changed).bind(uint64_t(reinterpret_cast<uintptr_t>(this)), id);
	Callable input = callable_mp_static(&TextSync::route_gui_input).bind(uint64_t(reinterpret_cast<uintptr_t>(this)), id);
	if (editor->is_connected("text_changed", changed)) editor->disconnect("text_changed", changed);
	if (editor->is_connected("gui_input", input)) editor->disconnect("gui_input", input);
	if (Doc *d = doc_for(it->second, false)) {
		d->editor = 0;
		// Closed without saving: the shared text is still the truth on disk.
		d->disk_dirty = true;
	}
	editors_.erase(it);
}

void TextSync::unbind_all() {
	std::vector<uint64_t> ids;
	for (const auto &kv : editors_) ids.push_back(kv.first);
	for (uint64_t id : ids) {
		if (CodeEdit *e = Object::cast_to<CodeEdit>(ObjectDB::get_instance(id))) {
			unbind(e);
		} else {
			if (Doc *d = doc_for(editors_[id], false)) d->editor = 0;
			editors_.erase(id);
		}
	}
}

void TextSync::route_text_changed(uint64_t self, uint64_t editor_id) {
	reinterpret_cast<TextSync *>(uintptr_t(self))->on_text_changed(editor_id);
}

void TextSync::route_gui_input(const Ref<InputEvent> &event, uint64_t self, uint64_t editor_id) {
	reinterpret_cast<TextSync *>(uintptr_t(self))->on_gui_input(event, editor_id);
}

void TextSync::on_text_changed(uint64_t editor_id) {
	if (applying_) return;
	auto it = editors_.find(editor_id);
	if (it == editors_.end()) return;
	Doc *d = doc_for(it->second, false);
	CodeEdit *e = d ? editor_of(*d) : nullptr;
	if (!e) return;
	String now = normalize_text(e->get_text());
	if (now == d->text) return;
	local_change(*d, TextOperation::diff(d->text, now), false);
}

void TextSync::on_gui_input(const Ref<InputEvent> &event, uint64_t editor_id) {
	Ref<InputEventKey> key = event;
	if (key.is_null() || !key->is_pressed()) return;
	bool undo = event->is_action_pressed("ui_undo", true, true);
	bool redo = event->is_action_pressed("ui_redo", true, true);
	if (!undo && !redo) return;
	auto it = editors_.find(editor_id);
	if (it == editors_.end()) return;
	Doc *d = doc_for(it->second, false);
	CodeEdit *e = d ? editor_of(*d) : nullptr;
	if (!e) return;
	// Your undo undoes your typing only, never a teammate's (the editor's own
	// undo would take back whatever came last).
	e->accept_event();
	undo_redo(*d, redo);
}

// Local edits

void TextSync::local_change(Doc &d, const TextOperation &op, bool from_undo) {
	if (op.is_noop()) return;
	String after;
	if (!op.apply(d.text, after)) return;
	if (!from_undo) {
		TextOperation inverse = op.invert(d.text);
		TextOperation grouped;
		if (!d.undo.empty() && now_ - d.last_edit < kUndoGroupSeconds && TextOperation::compose(inverse, d.undo.back(), grouped)) {
			d.undo.back() = grouped;
		} else {
			d.undo.push_back(inverse);
			if (d.undo.size() > kMaxUndo) d.undo.erase(d.undo.begin());
		}
		d.redo.clear();
		d.last_edit = now_;
	}
	d.text = after;
	last_typed_ = now_;
	// Typing while an edit is on its way is merged into one next edit.
	TextOperation merged;
	if (d.has_buffer && TextOperation::compose(d.buffer, op, merged)) {
		d.buffer = merged;
	} else {
		d.buffer = op;
		d.has_buffer = true;
	}
	send_next(d);
}

void TextSync::send_next(Doc &d) {
	if (d.has_outstanding || !hooks_.emit) return;
	TextOperation op;
	bool save = false;
	if (d.has_buffer) {
		op = d.buffer;
		d.has_buffer = false;
		d.buffer = TextOperation();
		save = d.save_after;
	} else if (d.save_after) {
		op.retain(d.server_text.length()); // nothing typed: only "saved"
		save = true;
	} else {
		return;
	}
	if (save) d.save_after = false;
	LocalOp lop;
	lop.type = "EditText";
	lop.target = Uuid::from_name(yhde_namespace(), String("file:") + d.path);
	lop.key = "@text";
	Dictionary payload;
	payload["s"] = d.path;
	payload["b"] = d.rev;
	payload["t"] = op.to_array();
	if (save) payload["save"] = true;
	lop.payload = payload;
	d.outstanding = op;
	d.has_outstanding = true;
	d.outstanding_ref = hooks_.emit(std::move(lop));
}

void TextSync::undo_redo(Doc &d, bool redo) {
	std::vector<TextOperation> &from = redo ? d.redo : d.undo;
	std::vector<TextOperation> &to = redo ? d.undo : d.redo;
	if (from.empty()) return;
	TextOperation op = from.back();
	from.pop_back();
	to.push_back(op.invert(d.text));
	// In the editor like a remote edit (its own undo history is not used for
	// shared files), then shared like typing.
	apply_to_editor(d, op);
	local_change(d, op, true);
}

void TextSync::forget(const String &path) {
	auto it = docs_.find(path);
	if (it == docs_.end()) return;
	if (CodeEdit *e = editor_of(it->second)) unbind(e);
	docs_.erase(path);
}

bool TextSync::saved(const String &path) {
	Doc *d = doc_for(path, false);
	if (!d || d->editor == 0) return false;
	auto echo = saved_by_us_.find(path);
	if (echo != saved_by_us_.end() && now_ - echo->second < 2.0) return true; // we saved it for a teammate
	d->save_after = true;
	send_next(*d);
	return true;
}

// Remote edits

void TextSync::transform_history(Doc &d, const TextOperation &remote) {
	for (std::vector<TextOperation> *stack : { &d.undo, &d.redo }) {
		TextOperation r = remote;
		std::vector<TextOperation> kept;
		for (auto it = stack->rbegin(); it != stack->rend(); ++it) {
			TextOperation e2, r2;
			if (!TextOperation::transform(*it, r, e2, r2)) {
				kept.clear(); // history no longer fits: start over
				break;
			}
			if (!e2.is_noop()) kept.push_back(e2);
			r = r2;
		}
		std::reverse(kept.begin(), kept.end());
		*stack = kept;
	}
}

void TextSync::apply_remote(const String &path, int64_t seq, const Dictionary &payload, bool own) {
	Doc *d = doc_for(path, true);
	if (!d) {
		if (hooks_.notice) hooks_.notice(String("Skipped a text edit of ") + path.get_file() + " (file not here)", 1);
		return;
	}
	if (seq <= d->rev) return; // already applied
	TextOperation op;
	if (!TextOperation::from_array(payload.get("t", Array()), op)) return;
	bool save = bool(payload.get("save", false));

	if (own && d->has_outstanding) {
		// Ours, as the server merged it.
		String next;
		if (!op.apply(d->server_text, next)) {
			if (hooks_.notice) hooks_.notice(String("Lost track of ") + path.get_file() + "; reopen it", 2);
			return;
		}
		d->server_text = next;
		d->rev = seq;
		d->has_outstanding = false;
		d->dirty = true;
		send_next(*d);
		if (d->editor == 0) d->disk_dirty = true;
		return;
	}

	String next;
	if (!op.apply(d->server_text, next)) {
		UtilityFunctions::push_warning("YHDE: a logged edit of ", path, " does not fit the local text (rev ", d->rev, ", edit ", seq, ")");
		return;
	}
	d->server_text = next;
	d->rev = seq;
	d->dirty = true;

	// Bring the remote edit over what we typed that the server has not seen yet.
	TextOperation remote = op;
	if (d->has_outstanding) {
		TextOperation r2, o2;
		if (TextOperation::transform(remote, d->outstanding, r2, o2)) {
			remote = r2;
			d->outstanding = o2;
		}
	}
	if (d->has_buffer) {
		TextOperation r2, b2;
		if (TextOperation::transform(remote, d->buffer, r2, b2)) {
			remote = r2;
			d->buffer = b2;
		}
	}
	transform_history(*d, remote);
	apply_to_editor(*d, remote);
	String shown;
	if (remote.apply(d->text, shown)) d->text = shown;

	if (d->editor == 0) {
		d->disk_dirty = true;
		if (save) d->reload_script = true;
	} else if (save) {
		save_open(*d);
	}
}

void TextSync::apply_to_editor(Doc &d, const TextOperation &op) {
	CodeEdit *e = editor_of(d);
	if (!e) return;
	applying_ = true;
	// Walk the text as it is before the edit, tracking line and column.
	const String &text = d.text;
	int64_t at = 0;
	int64_t line = 0;
	int64_t col = 0;
	auto advance = [&](int64_t n) {
		for (int64_t i = 0; i < n && at < text.length(); i++, at++) {
			if (text[at] == '\n') {
				line++;
				col = 0;
			} else {
				col++;
			}
		}
	};
	e->begin_complex_operation();
	for (const TextOperation::Component &c : op.components()) {
		switch (c.kind) {
			case TextOperation::Kind::Retain:
				advance(c.count);
				break;
			case TextOperation::Kind::Insert: {
				e->insert_text(c.text, int32_t(line), int32_t(col));
				// The inserted text is now before the cursor position.
				PackedStringArray parts = c.text.split("\n");
				if (parts.size() > 1) {
					line += parts.size() - 1;
					col = parts[parts.size() - 1].length();
				} else {
					col += c.text.length();
				}
				break;
			}
			case TextOperation::Kind::Delete: {
				int64_t l2 = line;
				int64_t c2 = col;
				for (int64_t i = 0; i < c.count && at + i < text.length(); i++) {
					if (text[at + i] == '\n') {
						l2++;
						c2 = 0;
					} else {
						c2++;
					}
				}
				e->remove_text(int32_t(line), int32_t(col), int32_t(l2), int32_t(c2));
				at += c.count;
				break;
			}
		}
	}
	e->end_complex_operation();
	// The editor's own undo would undo teammates' typing: shared files use ours.
	e->clear_undo_history();
	applying_ = false;
}

void TextSync::file_replaced(const String &path, int64_t seq) {
	auto it = docs_.find(path);
	if (it == docs_.end()) return;
	Doc &d = it->second;
	d.rev = seq;
	d.server_text = read_text(path);
	d.text = d.server_text;
	d.has_outstanding = false;
	d.has_buffer = false;
	d.undo.clear();
	d.redo.clear();
	d.dirty = true;
	if (CodeEdit *e = editor_of(d)) {
		applying_ = true;
		e->set_text(d.text);
		e->tag_saved_version();
		e->clear_undo_history();
		applying_ = false;
	}
}

void TextSync::on_rejected(const String &path, const Uuid &client_op_ref, const std::string &code) {
	Doc *d = doc_for(path, false);
	if (!d || !d->has_outstanding || d->outstanding_ref != client_op_ref) return;
	// Everything not yet confirmed is sent again against the current text.
	d->has_outstanding = false;
	d->has_buffer = false;
	TextOperation op = TextOperation::diff(d->server_text, d->text);
	if (!op.is_noop()) {
		d->buffer = op;
		d->has_buffer = true;
	}
	send_next(*d);
}

void TextSync::on_resubscribe() {
	// The queue sends unconfirmed operations again as they are; nothing to do
	// here, but a file whose edit was lost with the connection is re-diffed.
	for (auto &kv : docs_) {
		Doc &d = kv.second;
		if (!d.has_outstanding && !d.has_buffer) {
			TextOperation op = TextOperation::diff(d.server_text, d.text);
			if (!op.is_noop()) {
				d.buffer = op;
				d.has_buffer = true;
				send_next(d);
			}
		}
	}
}

// Files on disk

void TextSync::write_file(Doc &d) {
	d.disk_dirty = false;
	Ref<FileAccess> f = FileAccess::open(d.path, FileAccess::WRITE);
	if (f.is_null()) return;
	f->store_string(d.text);
	f.unref();
	if (hooks_.file_written) hooks_.file_written(d.path);
	if (d.reload_script) {
		d.reload_script = false;
		Ref<Script> script = set_cached_text(d.path, d.text);
		if (script.is_valid()) script->reload(true);
	}
}

void TextSync::save_open(Doc &d) {
	CodeEdit *e = editor_of(d);
	if (!e) return;
	saved_by_us_[d.path] = now_;
	// Saved through the Script / Shader resource when there is one, the way
	// its editor saves (no "file changed on disk" prompt afterwards).
	Ref<Resource> res = set_cached_text(d.path, e->get_text());
	if (res.is_valid()) {
		ResourceSaver::get_singleton()->save(res, d.path);
	} else {
		Ref<FileAccess> f = FileAccess::open(d.path, FileAccess::WRITE);
		if (f.is_valid()) f->store_string(e->get_text());
	}
	e->tag_saved_version();
	if (hooks_.file_written) hooks_.file_written(d.path);
}

void TextSync::process(double now) {
	now_ = now;
	for (auto &kv : docs_) {
		Doc &d = kv.second;
		if (d.editor != 0 && !editor_of(d)) {
			editors_.erase(d.editor);
			d.editor = 0;
			d.disk_dirty = true;
		}
		if (d.disk_dirty && d.editor == 0 && now - d.last_edit >= kWriteDelay) write_file(d);
	}
	// Where you are typing (presence).
	active_path_ = String();
	for (const auto &kv : editors_) {
		CodeEdit *e = Object::cast_to<CodeEdit>(ObjectDB::get_instance(kv.first));
		if (e && e->is_visible_in_tree() && e->has_focus()) active_path_ = kv.second;
	}
}

void TextSync::flush_if_dirty() {
	if (state_dir_.is_empty()) return;
	for (auto &kv : docs_) {
		Doc &d = kv.second;
		if (!d.dirty) continue;
		d.dirty = false;
		DirAccess::make_dir_recursive_absolute(state_dir_);
		Dictionary s;
		s["path"] = d.path;
		s["rev"] = d.rev;
		s["text"] = d.server_text;
		String file = state_path(d.path);
		Ref<FileAccess> f = FileAccess::open(file + ".tmp", FileAccess::WRITE);
		if (f.is_null()) continue;
		f->store_string(to_json(s));
		f.unref();
		DirAccess::rename_absolute(file + ".tmp", file);
	}
}

Dictionary TextSync::caret_state() const {
	Dictionary out;
	if (active_path_.is_empty()) return out;
	auto it = docs_.find(active_path_);
	if (it == docs_.end()) return out;
	CodeEdit *e = editor_of(it->second);
	if (!e) return out;
	out["script"] = active_path_;
	Array caret;
	caret.push_back(e->get_caret_line());
	caret.push_back(e->get_caret_column());
	out["caret"] = caret;
	if (e->has_selection()) {
		Array sel;
		sel.push_back(e->get_selection_from_line());
		sel.push_back(e->get_selection_from_column());
		sel.push_back(e->get_selection_to_line());
		sel.push_back(e->get_selection_to_column());
		out["tsel"] = sel;
	}
	return out;
}

} // namespace yhde
