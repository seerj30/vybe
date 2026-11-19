#!/usr/bin/env python3
"""Real-time spaces through Caddy (docs/design/p2-spaces-protocol.md), for infra/oracle/test-stack.sh.

Creates a guest, gets a one-time hub ticket, opens the hub WebSocket over HTTP/1.1 through Caddy, does the SignalR
MessagePack handshake, joins, holds the socket for longer than Caddy's server timeouts (read_body 30 s, write 60 s)
with pings, then leaves and joins again. Also checks that a foreign Origin and a reused ticket are refused.

Usage: spaces-check.py <origin https://localhost:PORT> <ca-file> [hold-seconds]
Standard library only.
"""

import base64
import http.client
import json
import os
import socket
import ssl
import sys
import time
import urllib.parse

ORIGIN, CA = sys.argv[1], sys.argv[2]
HOLD = int(sys.argv[3]) if len(sys.argv) > 3 else 65
URL = urllib.parse.urlsplit(ORIGIN)
CONTEXT = ssl.create_default_context(cafile=CA)
CONTEXT.set_alpn_protocols(["http/1.1"])


def fail(message):
    print(f"FAILED: spaces: {message}", file=sys.stderr)
    sys.exit(1)


def request(method, path, headers, body=None):
    connection = http.client.HTTPSConnection(URL.hostname, URL.port, context=CONTEXT, timeout=20)
    connection.request(method, path, body=body, headers=headers)
    response = connection.getresponse()
    return response.status, response.getheader("Set-Cookie") or "", response.read()


def ticket():
    status, cookie, _ = request("POST", "/api/v1/guests", {"Content-Type": "application/json", "X-Requested-With": "vybe-web"},
                                json.dumps({"birthYear": 1990, "birthMonth": 6}))
    if status != 201:
        fail(f"guest creation answered {status}")
    cookie = cookie.split(";", 1)[0]
    status, _, body = request("POST", "/api/v1/spaces/tickets", {"Cookie": cookie, "X-Requested-With": "vybe-web"})
    if status != 201:
        fail(f"ticket answered {status}")
    return json.loads(body)["ticket"]


def upgrade(token, origin):
    raw = socket.create_connection((URL.hostname, URL.port), timeout=20)
    sock = CONTEXT.wrap_socket(raw, server_hostname=URL.hostname)
    key = base64.b64encode(os.urandom(16)).decode()
    sock.sendall((f"GET /api/v1/spaces/hub?access_token={token} HTTP/1.1\r\nHost: {URL.netloc}\r\n"
                  f"Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\n"
                  f"Sec-WebSocket-Key: {key}\r\nOrigin: {origin}\r\n\r\n").encode())
    head = b""
    while b"\r\n\r\n" not in head:
        chunk = sock.recv(4096)
        if not chunk:
            break
        head += chunk
    status = int(head.split(b" ", 2)[1]) if head.startswith(b"HTTP/1.1 ") else 0
    return sock, status, head.split(b"\r\n\r\n", 1)[1] if b"\r\n\r\n" in head else b""


def send(sock, payload, opcode):
    mask = os.urandom(4)
    header = bytes([0x80 | opcode])
    header += bytes([0x80 | len(payload)]) if len(payload) < 126 else bytes([0x80 | 126]) + len(payload).to_bytes(2, "big")
    sock.sendall(header + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))


class Reader:
    def __init__(self, sock, pending):
        self.sock, self.buffer = sock, pending

    def take(self, n):
        while len(self.buffer) < n:
            chunk = self.sock.recv(65536)
            if not chunk:
                fail("the hub socket closed")
            self.buffer += chunk
        data, self.buffer = self.buffer[:n], self.buffer[n:]
        return data

    def frame(self):
        first, second = self.take(2)
        length = second & 0x7F
        if length == 126:
            length = int.from_bytes(self.take(2), "big")
        elif length == 127:
            length = int.from_bytes(self.take(8), "big")
        payload = self.take(length)
        if first & 0x0F == 0x8:
            fail(f"the hub sent a close frame: {payload!r}")
        return payload


PING = bytes([0x02, 0x91, 0x06])  # SignalR MessagePack: length 2, [6] (ping)
def join(invocation_id):
    """[1, {}, "<id>", "Join", []] with its length prefix."""
    return bytes([0x0B, 0x95, 0x01, 0x80, 0xA1, ord(invocation_id), 0xA4]) + b"Join" + bytes([0x90])


LEAVE = bytes([0x0B, 0x95, 0x01, 0x80, 0xC0, 0xA5]) + b"Leave" + bytes([0x90])  # [1, {}, nil, "Leave", []]


def welcome(reader):
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        if b"instanceId" in reader.frame():
            return
    fail("no welcome after Join")

foreign, status, _ = upgrade(ticket(), "https://evil.example")
foreign.close()
if status != 403:
    fail(f"a foreign Origin answered {status}, not 403")

token = ticket()
sock, status, rest = upgrade(token, ORIGIN)
if status != 101:
    fail(f"the upgrade answered {status}, not 101")
reused, status, _ = upgrade(token, ORIGIN)
reused.close()
if status != 401:
    fail(f"a reused ticket answered {status}, not 401")

reader = Reader(sock, rest)
send(sock, b'{"protocol":"messagepack","version":1}\x1e', 0x1)
if reader.frame() != b"{}\x1e":
    fail("no handshake response")

send(sock, join("1"), 0x2)
welcome(reader)

# Joined (no not_joined kick); hold past Caddy's server timeouts, with pings both ways.
start = time.monotonic()
while time.monotonic() - start < HOLD:
    send(sock, PING, 0x2)
    sock.settimeout(10)
    try:
        reader.frame()  # server pings every 15 s
    except socket.timeout:
        pass
    sock.settimeout(20)

send(sock, LEAVE, 0x2)
send(sock, join("2"), 0x2)
welcome(reader)
print(f"ok: spaces hub through Caddy (origin check, single-use ticket, join, {HOLD} s open, leave and join again)")
