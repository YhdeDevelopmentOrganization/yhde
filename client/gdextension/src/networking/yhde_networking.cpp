#include "networking/yhde_networking.h"

#include <godot_cpp/variant/packed_byte_array.hpp>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <random>

using namespace godot;

namespace yhde {

namespace {

constexpr double kPingInterval = 5.0;
constexpr double kReceiveTimeout = 20.0; // no traffic at all -> assume the link is dead
constexpr double kConnectTimeout = 10.0;
constexpr double kBackoffBase = 0.5;
constexpr double kBackoffMax = 10.0;
// Catch-up pages and snapshots can be large; the default 64 KiB would drop them.
constexpr int kInboundBuffer = 64 * 1024 * 1024;
constexpr int kOutboundBuffer = 64 * 1024 * 1024;
constexpr int kMaxQueuedPackets = 16384;
// Close code the server uses for a refused secret (sign-in, code or key).
constexpr int kUnauthorizedCloseCode = 4401;

double jitter() {
	static std::mt19937 rng{ std::random_device{}() };
	return std::uniform_real_distribution<double>(0.8, 1.2)(rng);
}

} // namespace

void YhdeNetworking::start(const String &url, const String &authorization) {
	url_ = url;
	headers_.clear();
	if (!authorization.is_empty()) headers_.push_back(String("Authorization: ") + authorization);
	attempts_ = 0;
	last_error_ = String();
	state_ = State::Backoff;
	retry_at_ = 0.0; // connect on next poll
}

void YhdeNetworking::stop() {
	if (ws_.is_valid()) {
		ws_->close(1000, "bye");
		ws_.unref();
	}
	state_ = State::Idle;
	attempts_ = 0;
}

void YhdeNetworking::restart(const String &reason) {
	if (state_ == State::Idle) return;
	close_socket(reason, 0.0);
}

double YhdeNetworking::seconds_until_retry(double now) const {
	return state_ == State::Backoff ? std::max(0.0, retry_at_ - now) : 0.0;
}

void YhdeNetworking::open_socket(double now) {
	ws_.instantiate();
	ws_->set_inbound_buffer_size(kInboundBuffer);
	ws_->set_outbound_buffer_size(kOutboundBuffer);
	ws_->set_max_queued_packets(kMaxQueuedPackets);
	ws_->set_handshake_headers(headers_);
	Error err = ws_->connect_to_url(url_);
	attempts_++;
	if (err != OK) {
		last_error_ = String("Could not open ") + url_ + " (error " + String::num_int64(int64_t(err)) + ")";
		ws_.unref();
		close_socket(last_error_, now);
		return;
	}
	state_ = State::Connecting;
	connect_started_ = now;
}

void YhdeNetworking::close_socket(const String &reason, double now) {
	bool was_open = state_ == State::Open;
	if (ws_.is_valid()) {
		ws_->close(1000, reason);
		ws_.unref();
	}
	if (!reason.is_empty()) last_error_ = reason;
	double delay = std::min(kBackoffMax, kBackoffBase * std::pow(2.0, double(std::max(0, attempts_ - 1)))) * jitter();
	state_ = State::Backoff;
	retry_at_ = now + delay;
	if (was_open && callbacks_.on_closed) callbacks_.on_closed(reason);
}

void YhdeNetworking::poll(double now) {
	switch (state_) {
		case State::Idle:
		case State::Refused:
			return;
		case State::Backoff:
			if (now >= retry_at_) open_socket(now);
			return;
		default:
			break;
	}
	if (ws_.is_null()) return;

	ws_->poll();
	WebSocketPeer::State ready = ws_->get_ready_state();

	if (state_ == State::Connecting) {
		if (ready == WebSocketPeer::STATE_OPEN) {
			ws_->set_no_delay(true); // small, latency-sensitive frames
			state_ = State::Open;
			attempts_ = 0;
			last_error_ = String();
			last_rx_ = now;
			last_ping_ = now;
			if (callbacks_.on_open) callbacks_.on_open();
		} else if (ready == WebSocketPeer::STATE_CLOSED) {
			if (ws_->get_close_code() == kUnauthorizedCloseCode) {
				refuse(ws_->get_close_reason());
				return;
			}
			String why = ws_->get_close_reason();
			close_socket(why.is_empty() ? String("Server unreachable at ") + url_ : why, now);
			return;
		} else if (now - connect_started_ > kConnectTimeout) {
			close_socket("Connection timed out", now);
			return;
		}
	}

	if (state_ != State::Open) return;

	while (ws_.is_valid() && ws_->get_available_packet_count() > 0) {
		PackedByteArray packet = ws_->get_packet();
		if (ws_->was_string_packet()) continue; // protocol is binary-only
		last_rx_ = now;
		handle_packet(packet, now);
		if (state_ != State::Open) return; // a handler may have restarted us
	}

	if (ws_->get_ready_state() == WebSocketPeer::STATE_CLOSED) {
		int code = ws_->get_close_code();
		if (code == kUnauthorizedCloseCode) {
			bool was_open = state_ == State::Open;
			refuse(ws_->get_close_reason());
			if (was_open && callbacks_.on_closed) callbacks_.on_closed(last_error_);
			return;
		}
		String why = ws_->get_close_reason();
		if (why.is_empty()) why = String("Connection closed (") + String::num_int64(code) + ")";
		close_socket(why, now);
		return;
	}

	if (now - last_rx_ > kReceiveTimeout) {
		close_socket("Server stopped responding", now);
		return;
	}

	if (now - last_ping_ >= kPingInterval) {
		last_ping_ = now;
		ping_sent_at_ = now;
		proto::Frame ping;
		ping.type = proto::MsgType::Ping;
		ping.channel = proto::Channel::System;
		uint64_t stamp = uint64_t(now * 1000.0);
		std::vector<uint8_t> token(8);
		std::memcpy(token.data(), &stamp, 8);
		ping.payload = proto::encode_ping(token);
		send(ping);
	}
}

void YhdeNetworking::refuse(const String &reason) {
	ws_.unref();
	state_ = State::Refused;
	last_error_ = reason.is_empty() ? String("sign-in or code refused") : reason;
}

void YhdeNetworking::handle_packet(const PackedByteArray &packet, double now) {
	proto::Frame frame;
	std::string err;
	if (!proto::decode_frame(packet.ptr(), size_t(packet.size()), frame, err)) {
		last_error_ = String("Malformed frame from server: ") + String::utf8(err.c_str());
		return;
	}
	if (frame.version != proto::kProtocolVersion) {
		close_socket("Server speaks an incompatible protocol version", now);
		return;
	}
	if (frame.type == proto::MsgType::Pong) {
		if (ping_sent_at_ > 0.0) rtt_ms_ = (now - ping_sent_at_) * 1000.0;
		return;
	}
	if (callbacks_.on_frame) callbacks_.on_frame(frame);
}

bool YhdeNetworking::send(const proto::Frame &frame) {
	if (state_ != State::Open || ws_.is_null()) return false;
	std::vector<uint8_t> wire = proto::encode_frame(frame);
	PackedByteArray bytes;
	bytes.resize(int64_t(wire.size()));
	if (!wire.empty()) std::memcpy(bytes.ptrw(), wire.data(), wire.size());
	return ws_->send(bytes, WebSocketPeer::WRITE_MODE_BINARY) == OK;
}

} // namespace yhde
