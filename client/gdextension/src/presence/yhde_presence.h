#pragma once

#include "core/uuid.h"
#include "networking/protocol.h"

#include <godot_cpp/variant/color.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/string.hpp>

#include <map>
#include <vector>

namespace yhde {

// Ephemeral awareness of other people (presence.md). Never persisted, never
// part of the operation log; last write wins.
struct Peer {
	Uuid session;
	Uuid member; // the person across sessions (for direct messages)
	godot::String name;
	godot::String scene;
	godot::String tool;
	godot::Dictionary state; // parsed StateJson: sel, cur, view, drag
	godot::Color color;
	double updated = 0.0;
};

class YhdePresence {
public:
	void clear() { peers_.clear(); }
	void set_self(const Uuid &session) { self_ = session; }

	// Returns true when the set of peers or their identity/location changed
	// (as opposed to just a cursor moving).
	bool apply(const proto::PresenceState &msg, double now);

	const std::map<Uuid, Peer> &peers() const { return peers_; }
	const Peer *find(const Uuid &id) const;
	godot::String name_of(const Uuid &session) const;

	static godot::Color color_for(const godot::String &name);
	static godot::String initials_for(const godot::String &name);

private:
	Uuid self_;
	std::map<Uuid, Peer> peers_;
};

} // namespace yhde
