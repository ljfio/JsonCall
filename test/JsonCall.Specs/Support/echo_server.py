import json
import sys


def read_message():
    headers = {}
    while True:
        line = sys.stdin.buffer.readline()
        if not line:
            return None
        line = line.strip()
        if not line:
            break
        key, value = line.decode().split(":", 1)
        headers[key.lower()] = value.strip()
    return json.loads(sys.stdin.buffer.read(int(headers["content-length"])))


sys.stderr.write("child ready\n")
sys.stderr.flush()

while True:
    message = read_message()
    if message is None:
        break
    sys.stderr.write("child handled " + message.get("method", "") + "\n")
    sys.stderr.flush()
    if "id" in message:
        body = json.dumps({"jsonrpc": "2.0", "id": message["id"], "result": message["params"][0]}).encode()
        sys.stdout.buffer.write(b"Content-Length: %d\r\n\r\n" % len(body) + body)
        sys.stdout.buffer.flush()
