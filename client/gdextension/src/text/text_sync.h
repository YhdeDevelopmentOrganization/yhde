#pragma once

#include "core/uuid.h"
#include "sync/sync_engine.h"
#include "text/text_operation.h"

#include <godot_cpp/classes/code_edit.hpp>
#include <godot_cpp/classes/input_event.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/string.hpp>

#include <functional>
#include <map>
#include <string>
#include <vector>

namespace yhde {

// Live co-editing of text files (text_editing.md): several people type into
// the same script at once, like in a shared document.
//
// Per file the log holds the file's bytes (an asset operation) followed by
// EditText operations the server has already merged. Here each file keeps:
//   server_text  the text after the last logged edit (`rev`)
//   text         what this editor shows: server_text + our unconfirmed edits
//   outstanding  our edit on its way to the server (at most one)
//   buffer       what we typed since, merged into one edit
// A logged edit from someone else is transformed over our unconfirmed ones
// before it is applied, so nobody's typing is lost or garbled.
//
// Files open in the script editor are bound to their CodeEdit: typing there
// becomes edits, remote edits are applied in place (carets stay where they
// are), and Ctrl+Z / Ctrl+Y undo and redo only your own typing.
class TextSync {
public:
	struct Hooks {
		// Queue an EditText operation; returns its client_op_ref.
		std::function<Uuid(LocalOp &&op)> emit;
		// Is this a shared text file right now (in the log, same bytes on disk)?
		// `seq` receives the log seq of its current bytes.
		std::function<bool(const godot::String &path, int64_t *seq)> shared;
		// This editor wrote the file itself (not a change to share as bytes).
		std::function<void(const godot::String &path)> file_written;
		std::function<void(const godot::String &text, int level)> notice;
	};

	void set_hooks(Hooks hooks) { hooks_ = std::move(hooks); }
	void configure(const godot::String &state_dir);
	void flush_if_dirty();
	void reset();
	void process(double now);

	static bool is_text_path(const godot::String &path);
	// Opened in the Shader editor (bottom panel), not the Script screen.
	static bool is_shader_path(const godot::String &path);

	// The UI maps script editor tabs to files (editor internals) and tells us.
	void bind(godot::CodeEdit *editor, const godot::String &path);
	void unbind(godot::CodeEdit *editor);
	void unbind_all();
	bool is_bound(const godot::String &path) const;

	// Incoming, in log order.
	void apply_remote(const godot::String &path, int64_t seq, const godot::Dictionary &payload, bool own);
	// The file got new bytes through the asset plane: its text starts over.
	void file_replaced(const godot::String &path, int64_t seq);
	// The file was deleted or moved away.
	void forget(const godot::String &path);
	void on_rejected(const godot::String &path, const Uuid &client_op_ref, const std::string &code);
	// The server connection was reset: unconfirmed edits will be sent again.
	void on_resubscribe();

	// You saved a shared text file: everyone's editor saves it too.
	bool saved(const godot::String &path);

	// For presence: where you are in which file.
	godot::Dictionary caret_state() const;
	double last_typed() const { return last_typed_; }

private:
	struct Doc {
		godot::String path;
		int64_t rev = 0;
		godot::String server_text;
		godot::String text;
		bool has_outstanding = false;
		TextOperation outstanding;
		Uuid outstanding_ref;
		bool has_buffer = false;
		TextOperation buffer;
		bool save_after = false;   // send a save marker once everything is confirmed
		uint64_t editor = 0;        // bound CodeEdit instance id
		std::vector<TextOperation> undo;
		std::vector<TextOperation> redo;
		double last_edit = 0.0;
		bool disk_dirty = false;    // closed file: write it out
		bool reload_script = false; // closed file: reload the cached Script (after a save marker)
		bool dirty = true;          // state to persist
	};

	Doc *doc_for(const godot::String &path, bool create);
	bool load_doc(Doc &d);
	void local_change(Doc &d, const TextOperation &op, bool from_undo);
	void send_next(Doc &d);
	void apply_to_editor(Doc &d, const TextOperation &op);
	void write_file(Doc &d);
	void save_open(Doc &d);
	godot::CodeEdit *editor_of(const Doc &d) const;
	godot::String state_path(const godot::String &path) const;
	static void route_text_changed(uint64_t self, uint64_t editor_id);
	static void route_gui_input(const godot::Ref<godot::InputEvent> &event, uint64_t self, uint64_t editor_id);
	void on_text_changed(uint64_t editor_id);
	void on_gui_input(const godot::Ref<godot::InputEvent> &event, uint64_t editor_id);
	void undo_redo(Doc &d, bool redo);
	void transform_history(Doc &d, const TextOperation &remote);

	Hooks hooks_;
	godot::String state_dir_;
	std::map<godot::String, Doc> docs_;
	std::map<uint64_t, godot::String> editors_; // CodeEdit id -> path
	bool applying_ = false;
	double now_ = 0.0;
	double last_typed_ = -100.0;
	godot::String active_path_;
	std::map<godot::String, double> saved_by_us_; // a save made because a teammate saved: not re-announced
};

} // namespace yhde
