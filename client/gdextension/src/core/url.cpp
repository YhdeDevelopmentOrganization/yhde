#include "core/url.h"

#include <cctype>

namespace yhde::url {

namespace {

std::string lower(std::string s) {
	for (char &c : s) c = char(std::tolower(static_cast<unsigned char>(c)));
	return s;
}

bool all_digits(const std::string &s) {
	if (s.empty()) return false;
	for (char c : s) {
		if (!std::isdigit(static_cast<unsigned char>(c))) return false;
	}
	return true;
}

} // namespace

Parts parse(const std::string &address) {
	Parts p;
	size_t sep = address.find("://");
	if (sep == std::string::npos || sep == 0) return p;
	std::string scheme = lower(address.substr(0, sep));
	for (char c : scheme) {
		if (!(std::isalnum(static_cast<unsigned char>(c)) || c == '+' || c == '-' || c == '.')) return p;
	}
	std::string rest = address.substr(sep + 3);
	std::string authority = rest.substr(0, rest.find_first_of("/?#"));
	// The last '@' ends the userinfo: "a@b@host" means host.
	size_t at = authority.rfind('@');
	if (at != std::string::npos) {
		p.has_userinfo = true;
		authority = authority.substr(at + 1);
	}
	std::string host;
	std::string port;
	if (!authority.empty() && authority[0] == '[') {
		size_t close = authority.find(']');
		if (close == std::string::npos || close == 1) return p;
		host = authority.substr(0, close + 1);
		std::string after = authority.substr(close + 1);
		if (!after.empty()) {
			if (after[0] != ':') return p;
			port = after.substr(1);
			if (!all_digits(port)) return p;
		}
	} else {
		size_t colon = authority.find(':');
		host = authority.substr(0, colon);
		if (colon != std::string::npos) {
			port = authority.substr(colon + 1);
			if (!all_digits(port)) return p;
		}
	}
	if (host.empty()) return p;
	p.scheme = scheme;
	p.host = lower(host);
	p.ok = true;
	return p;
}

std::string normalized(const std::string &address) {
	size_t sep = address.find("://");
	if (sep == std::string::npos) return address;
	return lower(address.substr(0, sep)) + address.substr(sep);
}

bool is_loopback_host(const std::string &host) {
	if (host == "localhost" || host == "[::1]") return true;
	// 127.0.0.0/8, written as four decimal parts.
	if (host.rfind("127.", 0) != 0) return false;
	int parts = 0;
	size_t start = 0;
	while (start <= host.size()) {
		size_t dot = host.find('.', start);
		std::string part = host.substr(start, dot == std::string::npos ? std::string::npos : dot - start);
		if (!all_digits(part) || part.size() > 3 || std::stoi(part) > 255) return false;
		parts++;
		if (dot == std::string::npos) break;
		start = dot + 1;
	}
	return parts == 4;
}

bool is_loopback(const std::string &address) {
	Parts p = parse(address);
	return p.ok && !p.has_userinfo && is_loopback_host(p.host);
}

} // namespace yhde::url
