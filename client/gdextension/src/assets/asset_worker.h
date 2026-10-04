#pragma once

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace yhde {

// Work that must not stall the editor: hashing files and moving bytes to and
// from the server's blob store (assets.md). Two threads: one hashes,
// one transfers, so a large upload never delays noticing the next change.
// Nothing here touches the scene tree or the project's resources.
struct AssetJob {
	enum class Kind {
		Hash,     // file -> hash, size
		Missing,  // hashes -> which ones the server lacks
		Upload,   // file (expected to hash to `hash`) -> blob store
		Download, // blob -> `file` (a part file, verified before success)
	};
	Kind kind = Kind::Hash;
	uint64_t id = 0;
	std::string file; // absolute, UTF-8
	std::string hash;
	int64_t size = 0;
	int64_t offset = 0; // upload: bytes the server already holds
	std::vector<std::string> hashes;
};

struct AssetResult {
	AssetJob::Kind kind = AssetJob::Kind::Hash;
	uint64_t id = 0;
	std::string file;
	std::string hash;
	int64_t size = 0;
	bool ok = false;
	bool transient = false; // network trouble: worth trying again
	bool mismatch = false;  // the bytes do not hash to what was expected
	std::string error;
	std::vector<std::string> missing;
	std::vector<std::pair<std::string, int64_t>> partial;
};

class AssetWorker {
public:
	~AssetWorker();

	// `server_url` is the editor's ws(s):// address; transfers use the same
	// host over http(s) with the same Authorization header.
	void start(const std::string &server_url, const std::string &authorization);
	void stop();
	bool running() const { return running_; }

	uint64_t submit(AssetJob job);
	bool poll(AssetResult &out);

	struct Progress {
		int uploads = 0;   // queued or running
		int downloads = 0; // queued or running
		int64_t bytes_done = 0;
		int64_t bytes_total = 0;
	};
	Progress progress() const;

	// ws://host:port/prefix/ws -> http://host:port/prefix
	static bool http_base(const std::string &server_url, bool &tls, std::string &host, int &port, std::string &prefix);

private:
	void hash_loop();
	void transfer_loop();
	void finish(AssetResult &&r);

	std::atomic<bool> running_{ false };
	std::atomic<bool> stop_{ false };
	std::thread hash_thread_;
	std::thread transfer_thread_;

	mutable std::mutex mutex_;
	std::condition_variable hash_cv_;
	std::condition_variable transfer_cv_;
	std::deque<AssetJob> hash_jobs_;
	std::deque<AssetJob> transfer_jobs_;
	std::deque<AssetResult> results_;
	uint64_t next_id_ = 1;

	std::string server_url_;
	std::string authorization_;

	// Progress of queued and running transfers.
	int uploads_ = 0;
	int downloads_ = 0;
	std::atomic<int64_t> bytes_done_{ 0 };
	std::atomic<int64_t> bytes_total_{ 0 };
};

} // namespace yhde
