#include "presence/yhde_presence.h"

#include "sync/variant_codec.h"

#include <godot_cpp/variant/char_string.hpp>
#include <godot_cpp/variant/packed_string_array.hpp>

using namespace godot;

namespace yhde {

bool YhdePresence::apply(const proto::PresenceState &msg, double now) {
	bool roster_changed = false;
	if (msg.full) {
		roster_changed = !peers_.empty() || !msg.entries.empty();
		peers_.clear();
	}
	for (const Uuid &left : msg.left) roster_changed = peers_.erase(left) > 0 || roster_changed;
	for (const proto::PresenceEntry &e : msg.entries) {
		if (e.session_id == self_) continue;
		Peer &p = peers_[e.session_id];
		String name = String::utf8(e.display_name.c_str(), int64_t(e.display_name.size()));
		String scene = String::utf8(e.scene.c_str(), int64_t(e.scene.size()));
		String tool = String::utf8(e.tool.c_str(), int64_t(e.tool.size()));
		if (p.session.is_nil() || p.name != name || p.scene != scene || p.tool != tool) roster_changed = true;
		if (p.member != e.member_id) roster_changed = true;
		p.session = e.session_id;
		p.member = e.member_id;
		p.name = name;
		p.scene = scene;
		p.tool = tool;
		p.color = color_for(name);
		p.updated = now;
		Variant state;
		String json = String::utf8(e.state_json.c_str(), int64_t(e.state_json.size()));
		if (from_json(json, state) && state.get_type() == Variant::DICTIONARY) {
			p.state = state;
		} else {
			p.state = Dictionary();
		}
		// One entry per person: a newer session of the same member (a
		// reconnect, while the server has not noticed the old one died yet)
		// replaces the older one instead of showing them twice.
		if (!e.member_id.is_nil()) {
			for (auto it = peers_.begin(); it != peers_.end();) {
				if (it->first != e.session_id && it->second.member == e.member_id) {
					it = peers_.erase(it);
					roster_changed = true;
				} else {
					++it;
				}
			}
		}
	}
	return roster_changed;
}

const Peer *YhdePresence::find(const Uuid &id) const {
	auto it = peers_.find(id);
	return it == peers_.end() ? nullptr : &it->second;
}

String YhdePresence::name_of(const Uuid &session) const {
	const Peer *p = find(session);
	return p ? p->name : String();
}

Color YhdePresence::color_for(const String &name) {
	// A calm, high-contrast palette that reads on dark and light editor themes.
	static const uint32_t palette[] = {
		0xF24E1EFF, // vermilion
		0x1ABCFEFF, // sky
		0x0ACF83FF, // mint
		0xA259FFFF, // violet
		0xFFC700FF, // amber
		0xFF7AB2FF, // pink
		0x00B5A6FF, // teal
		0x6E7CFFFF, // indigo
		0xFF8A3DFF, // orange
		0x5DD35DFF, // green
	};
	CharString utf8 = name.utf8();
	auto digest = sha1(reinterpret_cast<const uint8_t *>(utf8.get_data()), size_t(utf8.length()));
	uint32_t rgba = palette[digest[0] % (sizeof(palette) / sizeof(palette[0]))];
	return Color::hex(rgba);
}

String YhdePresence::initials_for(const String &name) {
	PackedStringArray words = name.strip_edges().split(" ", false);
	String out;
	for (int64_t i = 0; i < words.size() && out.length() < 2; i++) out += words[i].substr(0, 1).to_upper();
	if (out.length() == 1 && name.strip_edges().length() > 1 && words.size() == 1) {
		out += name.strip_edges().substr(1, 1).to_lower();
	}
	return out.is_empty() ? String("?") : out;
}

} // namespace yhde
