#pragma once

#include "networking/protocol.h"

#include <godot_cpp/classes/web_socket_peer.hpp>
#include <godot_cpp/variant/packed_string_array.hpp>
#include <godot_cpp/variant/string.hpp>

#include <functional>

namespace yhde {

// Owns the WSS connection: framing, heartbeat and automatic reconnect with
// jittered exponential backoff (network_protocol.md; reliability.md).
//
// It is transport only: it never interprets operations. Everything runs on the
// editor main thread from poll(); no threads, no locks.
class YhdeNetworking {
public:
	enum class State {
		Idle, // stop() called or never started
		Connecting,
		Open,
		Backoff, // waiting to retry
		Refused, // the server rejected our credentials; no retry until start()
	};

	struct Callbacks {
		std::function<void()> on_open;
		std::function<void(const godot::String &reason)> on_closed;
		std::function<void(const proto::Frame &frame)> on_frame;
	};

	void set_callbacks(Callbacks callbacks) { callbacks_ = std::move(callbacks); }

	// `authorization` is sent as the handshake's Authorization header when set.
	void start(const godot::String &url, const godot::String &authorization);
	void stop();
	void poll(double now);

	bool send(const proto::Frame &frame);
	// Forces a reconnect (e.g. the stream looks inconsistent).
	void restart(const godot::String &reason);

	State state() const { return state_; }
	bool is_open() const { return state_ == State::Open; }
	const godot::String &last_error() const { return last_error_; }
	int attempts() const { return attempts_; }
	double seconds_until_retry(double now) const;
	double round_trip_ms() const { return rtt_ms_; }

private:
	void open_socket(double now);
	void close_socket(const godot::String &reason, double now);
	void handle_packet(const godot::PackedByteArray &packet, double now);
	void refuse(const godot::String &reason);

	Callbacks callbacks_;
	godot::Ref<godot::WebSocketPeer> ws_;
	godot::String url_;
	godot::PackedStringArray headers_;
	State state_ = State::Idle;
	godot::String last_error_;
	int attempts_ = 0;
	double retry_at_ = 0.0;
	double connect_started_ = 0.0;
	double last_rx_ = 0.0;
	double last_ping_ = 0.0;
	double ping_sent_at_ = 0.0;
	double rtt_ms_ = -1.0;
};

} // namespace yhde
