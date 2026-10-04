#pragma once

#include "core/msgpack.h"
#include "core/uuid.h"

#include <cstdint>
#include <string>
#include <vector>

// YHDE wire protocol v1 (network_protocol.md). Mirrors the C# definitions in
// server/src/YHDE.Server/Framing: every message is a MessagePack positional
// array; fields are only ever appended, never repurposed.
namespace yhde::proto {

constexpr uint8_t kProtocolVersion = 1;
// The server's default inbound message cap (Yhde:MaxFrameSizeBytes).
constexpr size_t kMaxOutboundFrameBytes = 16u * 1024u * 1024u;

enum class MsgType : uint8_t {
	Hello = 0x01,
	Welcome = 0x02,
	Ping = 0x03,
	Pong = 0x04,
	Error = 0x05,
	Close = 0x06,
	Subscribe = 0x10,
	SyncState = 0x11,
	SubmitOp = 0x12,
	OpCommitted = 0x13,
	OpRejected = 0x14,
	UndoRequest = 0x15,
	Ack = 0x16,
	UndoResult = 0x17,
	PresenceUpdate = 0x20,
	PresenceState = 0x21,
	SocialRequest = 0x40,
	SocialEvent = 0x41,
};

enum class Channel : uint8_t {
	System = 0x00,
	Ops = 0x01,
	Presence = 0x02,
	AssetsCtrl = 0x03,
	Social = 0x04,
};

struct Frame {
	uint8_t version = kProtocolVersion;
	MsgType type = MsgType::Ping;
	Channel channel = Channel::System;
	uint8_t flags = 0;
	std::vector<uint8_t> client_op_ref;
	int64_t seq_or_ack = 0;
	std::vector<uint8_t> payload;
};

// [frame_len: uint32 BE][MessagePack Frame]
std::vector<uint8_t> encode_frame(const Frame &frame);
bool decode_frame(const uint8_t *data, size_t size, Frame &out, std::string &error);

// Payloads

struct Hello {
	std::vector<std::string> capabilities;
	std::string auth_token;
	Uuid member_id;            // per project folder; the account's id replaces it when signed in
	std::string display_name;
	std::string client_version; // the add-on's version
};

struct Welcome {
	uint8_t negotiated_version = 0;
	Uuid session_id;
	std::vector<std::string> server_capabilities;
	std::string server_version; // empty from servers older than 0.2.0
	// Set when connecting with an invite code: the project it opens.
	Uuid project_id;
	std::string project_name;
	Uuid branch_id;
};

struct Error {
	int64_t code = 0;
	std::string message;
	bool retryable = false;
};

struct Subscribe {
	Uuid project_id;
	Uuid branch_id;
	int64_t last_acked_seq = 0;
};

struct CommittedOp {
	Uuid op_id;
	int64_t seq = 0;
	Uuid branch_id;
	std::string type;
	Uuid target_id;
	std::string payload_json;
	Uuid actor_id;
	Uuid session_id;
	Uuid client_op_ref;
	int64_t parent_seq = 0;
	int64_t created_at_unix_ms = 0;
};

struct SyncState {
	Uuid branch_id;
	int64_t snapshot_seq = 0;
	std::vector<CommittedOp> tail;
	int64_t head_seq = 0;
	bool has_more = false;
};

struct SubmitOp {
	Uuid op_id;
	std::string type;
	Uuid target_id;
	std::string payload_json;
	Uuid client_op_ref;
	int64_t parent_seq = 0;
};

// Undo/redo of one editor action (operation_system.md): the inverse
// operations, committed atomically and linked to the operations they undo.
struct UndoRequest {
	Uuid request_id;
	std::string kind; // "undo" | "redo"
	std::vector<Uuid> undo_of;
	std::vector<SubmitOp> ops;
};

struct UndoResult {
	Uuid request_id;
	std::string status; // "committed" | "refused"
	std::string reason;
	std::string code;
};

struct OpRejected {
	Uuid client_op_ref;
	Uuid op_id;
	std::string reason;
	std::string code;
};

struct PresenceUpdate {
	std::string display_name;
	std::string scene;
	std::string tool;
	std::string state_json;
};

struct PresenceEntry {
	Uuid session_id;
	Uuid actor_id;
	std::string display_name;
	std::string scene;
	std::string tool;
	std::string state_json;
	int64_t updated_at_unix_ms = 0;
	Uuid member_id;
};

struct PresenceState {
	std::vector<PresenceEntry> entries;
	std::vector<Uuid> left;
	bool full = false;
};

// Chat and comments (social.md): JSON bodies, validated by the server.
struct SocialRequest {
	Uuid request_id;
	std::string kind;
	std::string body_json;
};

struct SocialEvent {
	std::string kind;
	std::string body_json;
	Uuid request_id; // set on the requester's own copy
};

// Encoders produce the Frame.payload bytes; decoders parse them back.
std::vector<uint8_t> encode(const Hello &m);
std::vector<uint8_t> encode(const Subscribe &m);
std::vector<uint8_t> encode(const SubmitOp &m);
std::vector<uint8_t> encode_ack(int64_t seq);
std::vector<uint8_t> encode_ping(const std::vector<uint8_t> &token);
std::vector<uint8_t> encode(const PresenceUpdate &m);
std::vector<uint8_t> encode(const UndoRequest &m);
std::vector<uint8_t> encode(const SocialRequest &m);

bool decode(const std::vector<uint8_t> &bytes, Welcome &out);
bool decode(const std::vector<uint8_t> &bytes, Error &out);
bool decode(const std::vector<uint8_t> &bytes, SyncState &out);
bool decode(const std::vector<uint8_t> &bytes, CommittedOp &out);
bool decode(const std::vector<uint8_t> &bytes, OpRejected &out);
bool decode(const std::vector<uint8_t> &bytes, PresenceState &out);
bool decode(const std::vector<uint8_t> &bytes, UndoResult &out);
bool decode(const std::vector<uint8_t> &bytes, SocialEvent &out);

} // namespace yhde::proto
