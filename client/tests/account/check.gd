extends SceneTree
## Account sign-in calls: the token goes only over https:// (or http:// to this
## computer), and a redirect is followed by hand to the same server only.
## Needs servers.py running (run.sh starts it). Prints "account: fails=N".

const Account := preload("res://addons/yhde/ui/account.gd")
const A := "http://127.0.0.1:18081"

var fails := 0
var checks := 0


func ok(cond: bool, label: String) -> void:
	checks += 1
	if not cond:
		fails += 1
		print("FAIL ", label)


func _initialize() -> void:
	await process_frame
	var table := {
		"https://yhde.example.com": true, "HTTPS://yhde.example.com": true,
		"http://localhost:8080": true, "http://127.0.0.1:18081": true, "http://127.0.0.2:18081": true, "http://[::1]:8080": true,
		"http://192.0.2.1:18081": false, "http://example.com": false, "http://127.evil.com": false,
		"http://127.0.0.1@evil.com": false, "http://evil.com/127.0.0.1": false, "http://localhost.evil.com": false,
		"ws://127.0.0.1": false, "": false, "ftp://localhost": false,
	}
	for b in table:
		ok(Account.allows_token(b) == table[b], "allows_token(%s) should be %s" % [b, table[b]])

	# Only pages on the server's own host open in the browser.
	var base := "https://yhde.example.com"
	var pages := {
		"https://yhde.example.com/app#/device?code=AB": true, "HTTPS://YHDE.example.com/x": true,
		"https://yhde.example.com:443/x": true, "http://yhde.example.com/x": false,
		"https://evil.com/app": false, "https://yhde.example.com.evil.com/": false,
		"https://evil.com@yhde.example.com/": false, "https://yhde.example.com@evil.com/": false,
		"file:///etc/passwd": false, "FILE:///C:/x.exe": false, "steam://run/1": false,
		"javascript:alert(1)": false, "": false, "yhde.example.com/app": false,
	}
	for page in pages:
		ok(Account.may_open(page, base) == pages[page], "may_open(%s) should be %s" % [page, pages[page]])
	ok(Account.may_open("http://127.0.0.1:18081/app", "http://127.0.0.1:18081"), "may_open on loopback http")
	ok(not Account.may_open("http://127.0.0.2:18081/app", "http://127.0.0.1:18081"), "another loopback host is not the server")
	ok(Account.host_of("wss://User@Host.Example:9/ws") == "host.example", "host_of drops user, port, path and case")
	ok(Account.host_of("ws://[::1]:8080/ws") == "[::1]", "host_of keeps IPv6 brackets")

	var a = Account.new()
	root.add_child(a)
	a.token = "SECRET-TOKEN"
	a.http_base = "http://192.0.2.1:18081"
	var t0 := Time.get_ticks_msec()
	var r: Dictionary = await a._request(HTTPClient.METHOD_GET, "/api/editor/projects", null, true)
	ok(r.code == -1, "plain http to another host is refused")
	ok(Time.get_ticks_msec() - t0 < 500, "refused without using the network")
	r = await a._request(HTTPClient.METHOD_POST, "/api/device/poll", {"deviceCode": "x"}, false)
	ok(r.code == -1, "refused even without the token (the poll answer carries one)")
	ok(a._error_text(r, "fallback").contains("unencrypted"), "the refusal text reaches the panel")

	a.http_base = A
	# [case, method, status, redirect target, expected code]. -1 = refused, 200 = followed.
	var cases := [
		["same307post", HTTPClient.METHOD_POST, 307, A + "/final?case=same307post", 200],
		["same308post", HTTPClient.METHOD_POST, 308, A + "/final?case=same308post", 200],
		["same303post", HTTPClient.METHOD_POST, 303, A + "/final?case=same303post", 200],
		["same302get", HTTPClient.METHOD_GET, 302, A + "/final?case=same302get", 200],
		["rel302get", HTTPClient.METHOD_GET, 302, "/final?case=rel302get", 200],
		["same302post", HTTPClient.METHOD_POST, 302, A + "/final?case=same302post", -1],
		["port307", HTTPClient.METHOD_POST, 307, "http://127.0.0.1:18082/final?case=port307", -1],
		["host307", HTTPClient.METHOD_POST, 307, "http://127.0.0.2:18081/final?case=host307", -1],
		["port302", HTTPClient.METHOD_GET, 302, "http://127.0.0.1:18082/final?case=port302", -1],
		["host303", HTTPClient.METHOD_GET, 303, "http://127.0.0.2:18081/final?case=host303", -1],
		["schemeless", HTTPClient.METHOD_GET, 302, "//127.0.0.1:18082/final?case=schemeless", -1],
		["prefix", HTTPClient.METHOD_GET, 302, "http://127.0.0.1:180810/final?case=prefix", -1],
	]
	for c in cases:
		var path: String = "/redir?code=%d&to=%s&case=%s" % [c[2], (c[3] as String).uri_encode(), c[0]]
		r = await a._request(c[1], path, {"a": 1} if c[1] == HTTPClient.METHOD_POST else null, true)
		ok(r.code == c[4], "%s: got %s, expected %s" % [c[0], r.code, c[4]])
	r = await a._request(HTTPClient.METHOD_GET, "/loop", null, true)
	ok(r.code == -1, "a redirect loop ends in a refusal")
	print("account: checks=%d fails=%d" % [checks, fails])
	quit(1 if fails > 0 else 0)
