#include "networking/protocol.h"

namespace yhde::proto {

namespace {

void write_uuid(MsgPackWriter &w, const Uuid &u) {
	std::array<uint8_t, 16> wire = u.to_wire();
	w.bin(wire.data(), wire.size());
}

Uuid read_uuid(const MpValue &v) {
	Uuid u;
	if (v.type == MpValue::BIN) {
		Uuid::from_wire(reinterpret_cast<const uint8_t *>(v.bytes.data()), v.bytes.size(), u);
	}
	return u;
}

std::string read_str(const MpValue &v) {
	return (v.type == MpValue::STR || v.type == MpValue::BIN) ? v.bytes : std::string();
}

bool parse(const std::vector<uint8_t> &bytes, MpValue &root) {
	std::string err;
	return msgpack_decode(bytes.data(), bytes.size(), root, err) && root.type == MpValue::ARRAY;
}

bool read_committed(const MpValue &v, CommittedOp &out) {
	if (v.type != MpValue::ARRAY) return false;
	out.op_id = read_uuid(v.at(0));
	out.seq = v.at(1).as_int();
	out.branch_id = read_uuid(v.at(2));
	out.type = read_str(v.at(3));
	out.target_id = read_uuid(v.at(4));
	out.payload_json = read_str(v.at(5));
	out.actor_id = read_uuid(v.at(6));
	out.session_id = read_uuid(v.at(7));
	out.client_op_ref = read_uuid(v.at(8));
	out.parent_seq = v.at(9).as_int();
	out.created_at_unix_ms = v.at(10).as_int();
	return out.seq > 0;
}

} // namespace

std::vector<uint8_t> encode_frame(const Frame &f) {
	MsgPackWriter w;
	w.buffer.reserve(f.payload.size() + 48);
	// Reserve the 4-byte length prefix; patched below.
	w.buffer.resize(4);
	w.array_header(7);
	w.integer(f.version);
	w.integer(uint8_t(f.type));
	w.integer(uint8_t(f.channel));
	w.integer(f.flags);
	w.bin(f.client_op_ref);
	w.integer(f.seq_or_ack);
	w.bin(f.payload);
	uint32_t len = uint32_t(w.buffer.size() - 4);
	w.buffer[0] = uint8_t(len >> 24);
	w.buffer[1] = uint8_t(len >> 16);
	w.buffer[2] = uint8_t(len >> 8);
	w.buffer[3] = uint8_t(len);
	return std::move(w.buffer);
}

bool decode_frame(const uint8_t *data, size_t size, Frame &out, std::string &error) {
	if (size < 4) {
		error = "frame shorter than its length prefix";
		return false;
	}
	uint32_t len = (uint32_t(data[0]) << 24) | (uint32_t(data[1]) << 16) | (uint32_t(data[2]) << 8) | uint32_t(data[3]);
	if (size_t(len) != size - 4) {
		error = "frame length mismatch";
		return false;
	}
	MpValue root;
	if (!msgpack_decode(data + 4, len, root, error)) return false;
	if (root.type != MpValue::ARRAY || root.size() < 7) {
		error = "frame is not a 7-element array";
		return false;
	}
	out.version = uint8_t(root.at(0).as_int());
	out.type = MsgType(uint8_t(root.at(1).as_int()));
	out.channel = Channel(uint8_t(root.at(2).as_int()));
	out.flags = uint8_t(root.at(3).as_int());
	out.client_op_ref = root.at(4).as_byte_vector();
	out.seq_or_ack = root.at(5).as_int();
	out.payload = root.at(6).as_byte_vector();
	return true;
}

std::vector<uint8_t> encode(const Hello &m) {
	MsgPackWriter w;
	w.array_header(6);
	w.integer(kProtocolVersion);
	w.array_header(uint32_t(m.capabilities.size()));
	for (const std::string &c : m.capabilities) w.str(c);
	if (m.auth_token.empty()) {
		w.nil();
	} else {
		w.str(m.auth_token);
	}
	write_uuid(w, m.member_id);
	w.str(m.display_name);
	w.str(m.client_version);
	return std::move(w.buffer);
}

std::vector<uint8_t> encode(const Subscribe &m) {
	MsgPackWriter w;
	w.array_header(3);
	write_uuid(w, m.project_id);
	write_uuid(w, m.branch_id);
	w.integer(m.last_acked_seq);
	return std::move(w.buffer);
}

static void write_submit(MsgPackWriter &w, const SubmitOp &m) {
	w.array_header(6);
	write_uuid(w, m.op_id);
	w.str(m.type);
	write_uuid(w, m.target_id);
	w.bin(reinterpret_cast<const uint8_t *>(m.payload_json.data()), m.payload_json.size());
	write_uuid(w, m.client_op_ref);
	w.integer(m.parent_seq);
}

std::vector<uint8_t> encode(const SubmitOp &m) {
	MsgPackWriter w;
	write_submit(w, m);
	return std::move(w.buffer);
}

std::vector<uint8_t> encode(const UndoRequest &m) {
	MsgPackWriter w;
	w.array_header(4);
	write_uuid(w, m.request_id);
	w.str(m.kind);
	w.array_header(uint32_t(m.undo_of.size()));
	for (const Uuid &id : m.undo_of) write_uuid(w, id);
	w.array_header(uint32_t(m.ops.size()));
	for (const SubmitOp &op : m.ops) write_submit(w, op);
	return std::move(w.buffer);
}

std::vector<uint8_t> encode_ack(int64_t seq) {
	MsgPackWriter w;
	w.array_header(1);
	w.integer(seq);
	return std::move(w.buffer);
}

std::vector<uint8_t> encode_ping(const std::vector<uint8_t> &token) {
	MsgPackWriter w;
	w.array_header(1);
	w.bin(token);
	return std::move(w.buffer);
}

std::vector<uint8_t> encode(const PresenceUpdate &m) {
	MsgPackWriter w;
	w.array_header(4);
	w.str(m.display_name);
	w.str(m.scene);
	w.str(m.tool);
	w.str(m.state_json);
	return std::move(w.buffer);
}

std::vector<uint8_t> encode(const SocialRequest &m) {
	MsgPackWriter w;
	w.array_header(3);
	write_uuid(w, m.request_id);
	w.str(m.kind);
	w.bin(reinterpret_cast<const uint8_t *>(m.body_json.data()), m.body_json.size());
	return std::move(w.buffer);
}

bool decode(const std::vector<uint8_t> &bytes, SocialEvent &out) {
	MpValue v;
	if (!parse(bytes, v)) return false;
	out.kind = read_str(v.at(0));
	out.body_json = read_str(v.at(1));
	out.request_id = read_uuid(v.at(2));
	return !out.kind.empty();
}

bool decode(const std::vector<uint8_t> &bytes, Welcome &out) {
	MpValue v;
	if (!parse(bytes, v)) return false;
	out.negotiated_version = uint8_t(v.at(0).as_int());
	out.session_id = read_uuid(v.at(1));
	out.server_capabilities.clear();
	for (const MpValue &c : v.at(2).items) out.server_capabilities.push_back(read_str(c));
	out.server_version = read_str(v.at(3));
	out.project_id = read_uuid(v.at(4));
	out.project_name = read_str(v.at(5));
	out.branch_id = read_uuid(v.at(6));
	return !out.session_id.is_nil();
}

bool decode(const std::vector<uint8_t> &bytes, Error &out) {
	MpValue v;
	if (!parse(bytes, v)) return false;
	out.code = v.at(0).as_int();
	out.message = read_str(v.at(1));
	out.retryable = v.at(2).as_bool();
	return true;
}

bool decode(const std::vector<uint8_t> &bytes, SyncState &out) {
	MpValue v;
	if (!parse(bytes, v)) return false;
	out.branch_id = read_uuid(v.at(0));
	out.snapshot_seq = v.at(1).as_int();
	out.tail.clear();
	for (const MpValue &item : v.at(3).items) {
		CommittedOp op;
		if (!read_committed(item, op)) return false;
		out.tail.push_back(std::move(op));
	}
	out.head_seq = v.at(4).as_int();
	out.has_more = v.at(5).as_bool(false);
	return true;
}

bool decode(const std::vector<uint8_t> &bytes, CommittedOp &out) {
	MpValue v;
	if (!parse(bytes, v)) return false;
	return read_committed(v, out);
}

bool decode(const std::vector<uint8_t> &bytes, OpRejected &out) {
	MpValue v;
	if (!parse(bytes, v)) return false;
	out.client_op_ref = read_uuid(v.at(0));
	out.op_id = read_uuid(v.at(1));
	out.reason = read_str(v.at(2));
	out.code = read_str(v.at(3));
	return true;
}

bool decode(const std::vector<uint8_t> &bytes, UndoResult &out) {
	MpValue v;
	if (!parse(bytes, v)) return false;
	out.request_id = read_uuid(v.at(0));
	out.status = read_str(v.at(1));
	out.reason = read_str(v.at(2));
	out.code = read_str(v.at(3));
	return true;
}

bool decode(const std::vector<uint8_t> &bytes, PresenceState &out) {
	MpValue v;
	if (!parse(bytes, v)) return false;
	out.entries.clear();
	out.left.clear();
	for (const MpValue &e : v.at(0).items) {
		if (e.type != MpValue::ARRAY) continue;
		PresenceEntry entry;
		entry.session_id = read_uuid(e.at(0));
		entry.actor_id = read_uuid(e.at(1));
		entry.display_name = read_str(e.at(2));
		entry.scene = read_str(e.at(3));
		entry.tool = read_str(e.at(4));
		entry.state_json = read_str(e.at(5));
		entry.updated_at_unix_ms = e.at(6).as_int();
		entry.member_id = read_uuid(e.at(7));
		if (!entry.session_id.is_nil()) out.entries.push_back(std::move(entry));
	}
	for (const MpValue &l : v.at(1).items) {
		Uuid u = read_uuid(l);
		if (!u.is_nil()) out.left.push_back(u);
	}
	out.full = v.at(2).as_bool(false);
	return true;
}

} // namespace yhde::proto
