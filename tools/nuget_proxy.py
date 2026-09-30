"""Temporary localhost NuGet V3 flat-container proxy for Windows TLS failures.

Run only while restoring this project. The upstream is the official NuGet API.
"""

import http.server
import json
import urllib.error
import urllib.request


HOST = "127.0.0.1"
PORT = 8765
UPSTREAM = "https://api.nuget.org/v3-flatcontainer/"


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/v3/index.json":
            body = json.dumps({"version": "3.0.0", "resources": [{
                "@id": f"http://{HOST}:{PORT}/flat/",
                "@type": "PackageBaseAddress/3.0.0",
            }]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if not self.path.startswith("/flat/") or ".." in self.path:
            self.send_error(404)
            return
        url = UPSTREAM + self.path[len("/flat/"):]
        try:
            with urllib.request.urlopen(url, timeout=90) as response:
                self.send_response(response.status)
                self.send_header("Content-Type", response.headers.get("Content-Type", "application/octet-stream"))
                if response.headers.get("Content-Length"):
                    self.send_header("Content-Length", response.headers["Content-Length"])
                self.end_headers()
                while chunk := response.read(1024 * 1024):
                    self.wfile.write(chunk)
        except urllib.error.HTTPError as error:
            self.send_error(error.code, error.reason)
        except (OSError, TimeoutError) as error:
            self.send_error(502, str(error))

    def log_message(self, format, *args):
        print(format % args, flush=True)


http.server.ThreadingHTTPServer((HOST, PORT), Handler).serve_forever()
