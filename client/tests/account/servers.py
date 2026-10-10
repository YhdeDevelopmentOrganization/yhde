"""Servers for the account redirect test. A (127.0.0.1:18081) redirects; B (127.0.0.1:18082) and
C (127.0.0.2:18081) are other origins. Every request to /final is logged to the file in argv[1].
Usage: servers.py LOG_FILE"""
import json, sys, threading, urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOG = sys.argv[1]
lock = threading.Lock()

def make(name):
    class H(BaseHTTPRequestHandler):
        def log_message(self, *a): pass
        def _do(self):
            u = urllib.parse.urlparse(self.path)
            q = urllib.parse.parse_qs(u.query)
            n = int(self.headers.get("Content-Length") or 0)
            if n: self.rfile.read(n)
            if u.path == "/loop":
                self.send_response(307)
                self.send_header("Location", "/loop")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            if u.path == "/redir":
                self.send_response(int(q["code"][0]))
                self.send_header("Location", q["to"][0])
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            rec = {"case": q.get("case", [""])[0], "server": name, "path": u.path, "method": self.command,
                   "auth": self.headers.get("Authorization"), "xyhde": self.headers.get("X-YHDE"), "host": self.headers.get("Host")}
            with lock:
                open(LOG, "a").write(json.dumps(rec) + "\n")
            body = b'{"ok":true}'
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        do_GET = do_POST = do_PUT = _do
    return H

def serve(host, port, name):
    s = ThreadingHTTPServer((host, port), make(name))
    threading.Thread(target=s.serve_forever, daemon=True).start()

serve("127.0.0.1", 18081, "A")
serve("127.0.0.1", 18082, "B")
serve("127.0.0.2", 18081, "C")
print("ready", flush=True)
threading.Event().wait()
