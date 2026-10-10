#pragma once

#include <string>

// Server addresses, parsed once instead of sliced in several places. Plain
// std::string so the rules can be tested without the editor.
namespace yhde::url {

struct Parts {
	bool ok = false;
	std::string scheme; // lowercase, without "://"
	std::string host; // lowercase; IPv6 keeps its brackets: "[::1]"
	bool has_userinfo = false; // "user:pass@host": which host is meant is not clear
};

// "scheme://[userinfo@]host[:port][/path][?query][#fragment]". `ok` is false
// when there is no scheme or no host, or the port or brackets are malformed.
Parts parse(const std::string &address);

// The same address with its scheme in lowercase (Godot refuses "WS://").
std::string normalized(const std::string &address);

// localhost, any 127.x.x.x and [::1]: this computer.
bool is_loopback_host(const std::string &host);

// True only for a well-formed address on this computer without userinfo.
bool is_loopback(const std::string &address);

} // namespace yhde::url
