#include "assets/asset_worker.h"

#include "core/sha256.h"

#include <godot_cpp/classes/http_client.hpp>
#include <godot_cpp/classes/json.hpp>
#include <godot_cpp/classes/tls_options.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>
#include <godot_cpp/variant/packed_string_array.hpp>

#include <chrono>
#include <cstring>
#include <functional>
#include <filesystem>
#include <fstream>

using namespace godot;

namespace yhde {

namespace {

constexpr int64_t kChunkBytes = 4 * 1024 * 1024;
constexpr double kStallSeconds = 30.0;
constexpr size_t kHashBlock = 1 << 20;

double seconds() {
	using namespace std::chrono;
	return duration<double>(steady_clock::now().time_since_epoch()).count();
}

std::filesystem::path fs_path(const std::string &utf8) {
	return std::filesystem::u8path(utf8);
}

String gs(const std::string &s) {
	return String::utf8(s.c_str(), int64_t(s.size()));
}

std::string ss(const String &s) {
	CharString c = s.utf8();
	return std::string(c.get_data(), size_t(c.length()));
}

// One keep-alive HTTP(S) connection to the server, driven synchronously on
// the transfer thread.
class Http {
public:
	Http(const std::string &server_url, const std::string &authorization, const std::atomic<bool> &stop) :
			authorization_(authorization), stop_(stop) {
		valid_ = AssetWorker::http_base(server_url, tls_, host_, port_, prefix_);
		client_.instantiate();
		client_->set_read_chunk_size(1 << 20);
	}

	bool valid() const { return valid_; }
	const std::string &prefix() const { return prefix_; }
	void set_project(const std::string &project) { project_ = project; }

	struct Response {
		int code = 0;
		Dictionary headers;
		std::string text; // small bodies (JSON)
		std::string error;
		bool transient = false;
	};
	// Receives the body as it arrives, with the status code; false stops reading.
	using Sink = std::function<bool(int code, const uint8_t *data, int64_t size)>;

	Response request(HTTPClient::Method method, const std::string &path, const std::vector<std::string> &extra,
			const PackedByteArray &body, const Sink &sink = nullptr) {
		Response r;
		if (!connect(r)) return r;
		PackedStringArray headers;
		if (!authorization_.empty()) headers.push_back(gs("Authorization: " + authorization_));
		if (!project_.empty()) headers.push_back(gs("X-YHDE-Project: " + project_));
		for (const std::string &h : extra) headers.push_back(gs(h));
		Error err = client_->request_raw(method, gs(prefix_ + path), headers, body);
		if (err != OK) return fail(r, "request failed");
		double last = seconds();
		while (client_->get_status() == HTTPClient::STATUS_REQUESTING) {
			if (!pump(last, r)) return r;
		}
		if (!client_->has_response()) return fail(r, "no response");
		r.code = int(client_->get_response_code());
		r.headers = client_->get_response_headers_as_dictionary();
		last = seconds();
		while (client_->get_status() == HTTPClient::STATUS_BODY) {
			PackedByteArray chunk = client_->read_response_body_chunk();
			if (chunk.is_empty()) {
				if (!pump(last, r)) return r;
				continue;
			}
			last = seconds();
			if (sink) {
				if (!sink(r.code, chunk.ptr(), chunk.size())) {
					client_->close(); // the rest of the body is not wanted
					return r;
				}
			} else if (r.text.size() < (1u << 20)) {
				r.text.append(reinterpret_cast<const char *>(chunk.ptr()), size_t(chunk.size()));
			}
		}
		return r;
	}

	void close() { client_->close(); }

private:
	Response &fail(Response &r, const char *what) {
		r.error = what;
		r.transient = true;
		client_->close();
		return r;
	}

	// One poll step; false once stopped, stalled or disconnected.
	bool pump(double &last, Response &r) {
		if (stop_) {
			fail(r, "stopped");
			return false;
		}
		HTTPClient::Status before = client_->get_status();
		client_->poll();
		HTTPClient::Status now = client_->get_status();
		if (now == HTTPClient::STATUS_CONNECTION_ERROR || now == HTTPClient::STATUS_DISCONNECTED ||
				now == HTTPClient::STATUS_TLS_HANDSHAKE_ERROR || now == HTTPClient::STATUS_CANT_CONNECT ||
				now == HTTPClient::STATUS_CANT_RESOLVE) {
			fail(r, "connection lost");
			return false;
		}
		if (now != before) last = seconds();
		if (seconds() - last > kStallSeconds) {
			fail(r, "timed out");
			return false;
		}
		std::this_thread::sleep_for(std::chrono::milliseconds(1));
		return true;
	}

	bool connect(Response &r) {
		if (!valid_) {
			r.error = "unsupported server address";
			return false;
		}
		if (client_->get_status() == HTTPClient::STATUS_CONNECTED) return true;
		client_->close();
		Ref<TLSOptions> options = tls_ ? TLSOptions::client() : Ref<TLSOptions>();
		if (client_->connect_to_host(gs(host_), port_, options) != OK) {
			fail(r, "cannot connect");
			return false;
		}
		double last = seconds();
		for (;;) {
			HTTPClient::Status s = client_->get_status();
			if (s == HTTPClient::STATUS_CONNECTED) return true;
			if (s != HTTPClient::STATUS_RESOLVING && s != HTTPClient::STATUS_CONNECTING) {
				fail(r, "cannot connect");
				return false;
			}
			if (stop_ || seconds() - last > kStallSeconds) {
				fail(r, stop_ ? "stopped" : "connect timed out");
				return false;
			}
			client_->poll();
			std::this_thread::sleep_for(std::chrono::milliseconds(1));
		}
	}

	Ref<HTTPClient> client_;
	std::string authorization_;
	std::string project_;
	const std::atomic<bool> &stop_;
	bool valid_ = false;
	bool tls_ = false;
	std::string host_;
	int port_ = -1;
	std::string prefix_;
};

Dictionary parse_json(const std::string &text) {
	Variant v = JSON::parse_string(gs(text));
	return v.get_type() == Variant::DICTIONARY ? Dictionary(v) : Dictionary();
}

bool hash_file(const std::string &file, std::string &hash, int64_t &size, std::string &error) {
	std::ifstream in(fs_path(file), std::ios::binary);
	if (!in) {
		error = "cannot read file";
		return false;
	}
	Sha256 sha;
	std::vector<char> buffer(kHashBlock);
	size = 0;
	while (in) {
		in.read(buffer.data(), std::streamsize(buffer.size()));
		std::streamsize n = in.gcount();
		if (n <= 0) break;
		sha.update(buffer.data(), size_t(n));
		size += n;
	}
	if (in.bad()) {
		error = "read error";
		return false;
	}
	hash = sha.finish_hex();
	return true;
}

} // namespace

bool AssetWorker::http_base(const std::string &server_url, bool &tls, std::string &host, int &port, std::string &prefix) {
	std::string rest;
	if (server_url.rfind("wss://", 0) == 0) {
		tls = true;
		rest = server_url.substr(6);
	} else if (server_url.rfind("ws://", 0) == 0) {
		tls = false;
		rest = server_url.substr(5);
	} else if (server_url.rfind("https://", 0) == 0) {
		tls = true;
		rest = server_url.substr(8);
	} else if (server_url.rfind("http://", 0) == 0) {
		tls = false;
		rest = server_url.substr(7);
	} else {
		return false;
	}
	size_t slash = rest.find('/');
	std::string authority = slash == std::string::npos ? rest : rest.substr(0, slash);
	std::string path = slash == std::string::npos ? std::string() : rest.substr(slash);
	size_t query = path.find_first_of("?#");
	if (query != std::string::npos) path = path.substr(0, query);
	if (authority.empty() || authority.find('@') != std::string::npos) return false;

	port = tls ? 443 : 80;
	if (authority[0] == '[') {
		size_t close = authority.find(']');
		if (close == std::string::npos) return false;
		host = authority.substr(1, close - 1);
		if (close + 1 < authority.size()) {
			if (authority[close + 1] != ':') return false;
			port = std::atoi(authority.c_str() + close + 2);
		}
	} else {
		size_t colon = authority.rfind(':');
		host = authority.substr(0, colon);
		if (colon != std::string::npos) port = std::atoi(authority.c_str() + colon + 1);
	}
	if (host.empty() || port <= 0 || port > 65535) return false;

	// The WebSocket lives at <prefix>/ws; the asset routes at <prefix>/assets.
	while (!path.empty() && path.back() == '/') path.pop_back();
	if (path.size() >= 3 && path.compare(path.size() - 3, 3, "/ws") == 0) path.resize(path.size() - 3);
	prefix = path;
	return true;
}

AssetWorker::~AssetWorker() {
	stop();
}

void AssetWorker::start(const std::string &server_url, const std::string &authorization) {
	stop();
	server_url_ = server_url;
	authorization_ = authorization;
	stop_ = false;
	running_ = true;
	hash_thread_ = std::thread(&AssetWorker::hash_loop, this);
	transfer_thread_ = std::thread(&AssetWorker::transfer_loop, this);
}

void AssetWorker::stop() {
	if (!running_) return;
	{
		std::lock_guard<std::mutex> lock(mutex_);
		stop_ = true;
	}
	hash_cv_.notify_all();
	transfer_cv_.notify_all();
	if (hash_thread_.joinable()) hash_thread_.join();
	if (transfer_thread_.joinable()) transfer_thread_.join();
	std::lock_guard<std::mutex> lock(mutex_);
	hash_jobs_.clear();
	transfer_jobs_.clear();
	results_.clear();
	uploads_ = 0;
	downloads_ = 0;
	bytes_done_ = 0;
	bytes_total_ = 0;
	running_ = false;
}

void AssetWorker::set_project(const std::string &project_id) {
	std::lock_guard<std::mutex> lock(mutex_);
	project_ = project_id;
}

uint64_t AssetWorker::submit(AssetJob job) {
	std::lock_guard<std::mutex> lock(mutex_);
	job.id = next_id_++;
	job.project = project_;
	uint64_t id = job.id;
	if (job.kind == AssetJob::Kind::Hash) {
		hash_jobs_.push_back(std::move(job));
		hash_cv_.notify_one();
	} else {
		if (job.kind == AssetJob::Kind::Upload) {
			uploads_++;
			bytes_total_ += job.size - job.offset;
		} else if (job.kind == AssetJob::Kind::Download) {
			downloads_++;
			bytes_total_ += job.size;
		}
		transfer_jobs_.push_back(std::move(job));
		transfer_cv_.notify_one();
	}
	return id;
}

bool AssetWorker::poll(AssetResult &out) {
	std::lock_guard<std::mutex> lock(mutex_);
	if (results_.empty()) return false;
	out = std::move(results_.front());
	results_.pop_front();
	return true;
}

AssetWorker::Progress AssetWorker::progress() const {
	std::lock_guard<std::mutex> lock(mutex_);
	Progress p;
	p.uploads = uploads_;
	p.downloads = downloads_;
	p.bytes_done = bytes_done_;
	p.bytes_total = bytes_total_;
	return p;
}

void AssetWorker::finish(AssetResult &&r) {
	std::lock_guard<std::mutex> lock(mutex_);
	if (r.kind == AssetJob::Kind::Upload) uploads_--;
	if (r.kind == AssetJob::Kind::Download) downloads_--;
	if (uploads_ == 0 && downloads_ == 0) {
		bytes_done_ = 0;
		bytes_total_ = 0;
	}
	results_.push_back(std::move(r));
}

void AssetWorker::hash_loop() {
	for (;;) {
		AssetJob job;
		{
			std::unique_lock<std::mutex> lock(mutex_);
			hash_cv_.wait(lock, [this] { return stop_ || !hash_jobs_.empty(); });
			if (stop_) return;
			job = std::move(hash_jobs_.front());
			hash_jobs_.pop_front();
		}
		AssetResult r;
		r.kind = job.kind;
		r.id = job.id;
		r.file = job.file;
		r.ok = hash_file(job.file, r.hash, r.size, r.error);
		finish(std::move(r));
	}
}

void AssetWorker::transfer_loop() {
	Http http(server_url_, authorization_, stop_);
	for (;;) {
		AssetJob job;
		{
			std::unique_lock<std::mutex> lock(mutex_);
			transfer_cv_.wait(lock, [this] { return stop_ || !transfer_jobs_.empty(); });
			if (stop_) return;
			job = std::move(transfer_jobs_.front());
			transfer_jobs_.pop_front();
		}
		http.set_project(job.project);
		AssetResult r;
		r.kind = job.kind;
		r.id = job.id;
		r.file = job.file;
		r.hash = job.hash;
		r.size = job.size;

		switch (job.kind) {
			case AssetJob::Kind::Missing: {
				Array list;
				for (const std::string &h : job.hashes) list.push_back(gs(h));
				Dictionary body;
				body["hashes"] = list;
				CharString json = JSON::stringify(body).utf8();
				PackedByteArray bytes;
				bytes.resize(json.length());
				memcpy(bytes.ptrw(), json.get_data(), size_t(json.length()));
				Http::Response res = http.request(HTTPClient::METHOD_POST, "/assets/missing", { "Content-Type: application/json" }, bytes);
				if (res.code == 200) {
					Dictionary d = parse_json(res.text);
					Array missing = d.get("missing", Array());
					for (int64_t i = 0; i < missing.size(); i++) r.missing.push_back(ss(missing[i]));
					Dictionary partial = d.get("partial", Dictionary());
					Array keys = partial.keys();
					for (int64_t i = 0; i < keys.size(); i++) r.partial.emplace_back(ss(keys[i]), int64_t(partial[keys[i]]));
					r.ok = true;
				} else {
					r.error = res.error.empty() ? "server answered " + std::to_string(res.code) : res.error;
					r.transient = res.code == 0 || res.code >= 500;
				}
				break;
			}

			case AssetJob::Kind::Upload: {
				std::ifstream in(fs_path(job.file), std::ios::binary);
				if (!in) {
					r.error = "cannot read file";
					r.mismatch = true; // gone or unreadable: rescan it
					break;
				}
				int64_t offset = job.offset;
				int conflicts = 0;
				bool done = false;
				while (!done && !stop_) {
					int64_t n = std::min<int64_t>(kChunkBytes, job.size - offset);
					PackedByteArray chunk;
					chunk.resize(n);
					in.clear();
					in.seekg(std::streamoff(offset));
					if (n > 0) in.read(reinterpret_cast<char *>(chunk.ptrw()), std::streamsize(n));
					if (n > 0 && in.gcount() != n) {
						r.error = "file changed while uploading";
						r.mismatch = true;
						break;
					}
					Http::Response res = http.request(HTTPClient::METHOD_PATCH, "/assets/blobs/" + job.hash,
							{ "Content-Type: application/offset+octet-stream", "Upload-Offset: " + std::to_string(offset),
									"Upload-Length: " + std::to_string(job.size) },
							chunk);
					Dictionary d = parse_json(res.text);
					if (res.code == 200) {
						int64_t next = int64_t(d.get("offset", Variant(int64_t(offset + n))));
						bytes_done_ += std::max<int64_t>(0, next - offset);
						offset = next;
						done = bool(d.get("complete", false));
						if (!done && offset >= job.size) {
							r.error = "upload did not complete";
							r.transient = true;
							break;
						}
					} else if (res.code == 409 && conflicts++ < 3) {
						offset = int64_t(d.get("offset", Variant(int64_t(0))));
					} else if (res.code == 422) {
						r.error = "file changed while uploading";
						r.mismatch = true;
						break;
					} else {
						// The server says why (out of storage, low on disk, view only).
						String detail = d.get("detail", String());
						r.error = !detail.is_empty() ? ss(detail)
								: res.error.empty()  ? "server answered " + std::to_string(res.code)
													 : res.error;
						// 507 (storage full) does not pass by trying again soon.
						r.transient = res.code == 0 || res.code == 423 || (res.code >= 500 && res.code != 507);
						break;
					}
				}
				r.ok = done;
				break;
			}

			case AssetJob::Kind::Download: {
				std::filesystem::path part = fs_path(job.file);
				std::error_code ec;
				std::filesystem::create_directories(part.parent_path(), ec);
				int64_t have = 0;
				Sha256 sha;
				if (std::filesystem::exists(part, ec)) {
					have = int64_t(std::filesystem::file_size(part, ec));
					if (ec || have > job.size) {
						std::filesystem::remove(part, ec);
						have = 0;
					} else if (have > 0) {
						std::ifstream prefix(part, std::ios::binary);
						std::vector<char> buffer(kHashBlock);
						int64_t left = have;
						while (left > 0 && prefix) {
							prefix.read(buffer.data(), std::streamsize(std::min<int64_t>(left, int64_t(buffer.size()))));
							std::streamsize n = prefix.gcount();
							if (n <= 0) break;
							sha.update(buffer.data(), size_t(n));
							left -= n;
						}
						if (left != 0) {
							sha.reset();
							std::filesystem::remove(part, ec);
							have = 0;
						}
					}
				}
				bytes_done_ += have;
				int64_t got = have;
				std::ofstream out;
				bool write_failed = false;
				bool opened = false;
				std::vector<std::string> headers;
				if (have > 0 && have < job.size) headers.push_back("Range: bytes=" + std::to_string(have) + "-");
				Http::Response res;
				if (have < job.size) {
					res = http.request(HTTPClient::METHOD_GET, "/assets/blobs/" + job.hash, headers, PackedByteArray(),
							[&](int code, const uint8_t *data, int64_t size) {
								if (code != 200 && code != 206) return true; // an error body: not file content
								if (!opened) {
									if (code == 200 && got > 0) {
										// The whole blob instead of the rest: start over.
										bytes_done_ -= got;
										got = 0;
										sha.reset();
										out.open(part, std::ios::binary | std::ios::trunc);
									} else {
										out.open(part, std::ios::binary | std::ios::app);
									}
									opened = bool(out);
									if (!opened) {
										write_failed = true;
										return false;
									}
								}
								if (got + size > job.size) return false;
								out.write(reinterpret_cast<const char *>(data), std::streamsize(size));
								if (!out) {
									write_failed = true;
									return false;
								}
								sha.update(data, size_t(size));
								got += size;
								bytes_done_ += size;
								return true;
							});
				}
				out.close();
				if (have < job.size && res.code != 200 && res.code != 206) {
					r.error = res.error.empty() ? "server answered " + std::to_string(res.code) : res.error;
					r.transient = res.code == 0 || res.code >= 500;
					break;
				}
				if (write_failed) {
					r.error = "cannot write " + job.file;
					break;
				}
				if (got != job.size) {
					r.error = "download incomplete";
					r.transient = true;
					break;
				}
				if (job.size == 0) {
					std::ofstream empty(part, std::ios::binary | std::ios::trunc);
				}
				if (sha.finish_hex() != job.hash) {
					std::filesystem::remove(part, ec);
					r.error = "downloaded bytes did not verify";
					r.mismatch = true;
					break;
				}
				r.ok = true;
				break;
			}

			case AssetJob::Kind::Hash:
				break;
		}
		finish(std::move(r));
	}
}

} // namespace yhde
