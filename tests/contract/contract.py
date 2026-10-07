#!/usr/bin/env python3
"""Records how a ZapMQ server answers the 1.x protocol.

    contract.py record  http://host:5679  out.json
    contract.py compare reference.json other.json

Run `record` against a 1.x (Delphi) server and against a 2.x server, then `compare` the two
files. Only queues named zapmqContract<random> are touched.
"""
import http.client
import json
import re
import sys
import time
import uuid
from urllib.parse import quote, urlsplit

PREFIX = "/datasnap/rest/TZapMethods/"
ID = re.compile(r"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")
VOLATILE_HEADERS = {"date", "content-length"}
# Where 2.x answers differently from 1.x on purpose: a TTL above 16 bits is accepted.
INTENTIONAL = {"ttl_67536.publish", "ttl_67536.get_after_4s"}

ANSWER = json.dumps({"answer": 42, "text": "a\u00e7\u00e3o"}, ensure_ascii=False)
N1, N2 = '{"n":1}', '{"n":2}'
POSTED = '{"Id":"","Body":{"v":1},"RPC":false,"TTL":0}'
NO_ID = "{00000000-0000-0000-0000-000000000000}"
EMPTY, NOT_JSON, ARRAY = "{}", "not json", "[1]"


class Server:
    def __init__(self, base):
        parts = urlsplit(base)
        self.host, self.port = parts.hostname, parts.port or 80
        self.last_id = None

    def call(self, raw_path, method="GET"):
        """Sends the path exactly as given (no client-side normalisation) and returns the exchange."""
        connection = http.client.HTTPConnection(self.host, self.port, timeout=15)
        try:
            connection.putrequest(method, PREFIX + raw_path, skip_host=True, skip_accept_encoding=True)
            connection.putheader("Host", f"{self.host}:{self.port}")
            if method != "GET":
                connection.putheader("Content-Length", "0")
            connection.endheaders()
            response = connection.getresponse()
            body = response.read().decode("utf-8", errors="replace")
            headers = {k.lower(): v for k, v in response.getheaders()}
        finally:
            connection.close()

        found = ID.search(body)
        if found:
            self.last_id = found.group(0)
        return {"status": response.status, "headers": headers, "body": body}


def enc(text):
    return quote(text, safe="")


def scenarios(server):
    """Yields (name, exchange). Later steps of a scenario reuse the queue and the id of earlier ones."""
    def queue():
        return "zapmqContract" + uuid.uuid4().hex[:12]

    def publish(q, payload):
        return server.call(f"UpdateMessage/{q}/{enc(payload)}")

    q = queue()
    yield "get_message.unknown_queue", server.call(f"GetMessage/{q}")

    # Exactly what the .NET wrapper sends.
    q = queue()
    yield "plain.publish", publish(q, '{"Id":null,"Body":{"name":"x","n":1.50,"b":true,"z":null},"RPC":false,"TTL":0,"Response":null}')
    yield "plain.get", server.call(f"GetMessage/{q}")
    yield "plain.get_again", server.call(f"GetMessage/{q}")

    q = queue()
    yield "text.publish", publish(q, json.dumps(
        {"Id": "", "Body": {"path": "D:\\files\\a b/c.bmp", "url": "http://h:80/x?y=1&z=2#f",
                            "text": "a\u00e7\u00e3o \u20ac + % \"q\" <tag> 'single'", "tab": "a\tb\nc"},
         "RPC": False, "TTL": 0}, ensure_ascii=False))
    yield "text.get", server.call(f"GetMessage/{q}")

    q = queue()
    yield "escaped_unicode.publish", publish(q, '{"Id":"","Body":{"text":"a\\u00e7\\u00e3o"},"RPC":false,"TTL":0}')
    yield "escaped_unicode.get", server.call(f"GetMessage/{q}")

    q = queue()
    yield "whitespace.publish", publish(q, '{ "Id" : "" , "Body" : { "a" : [ 1 , 2 ] , "b" : { } } , "RPC" : false , "TTL" : 0 }')
    yield "whitespace.get", server.call(f"GetMessage/{q}")

    for name, body in [("string", '"text"'), ("array", "[1,2]"), ("number", "5"), ("null", "null")]:
        q = queue()
        yield f"body_{name}.publish", publish(q, '{"Id":"","Body":' + body + ',"RPC":false,"TTL":0}')
        yield f"body_{name}.get", server.call(f"GetMessage/{q}")

    for name, payload in [
        ("no_id", '{"Body":{"a":1},"RPC":false,"TTL":0}'),
        ("no_body", '{"Id":"","RPC":false,"TTL":0}'),
        ("no_rpc", '{"Id":"","Body":{"a":1},"TTL":0}'),
        ("no_ttl", '{"Id":"","Body":{"a":1},"RPC":false}'),
        ("client_id", '{"Id":"mine","Body":{"a":1},"RPC":false,"TTL":0}'),
        ("empty_object", "{}"),
    ]:
        q = queue()
        yield f"fields_{name}.publish", publish(q, payload)
        yield f"fields_{name}.get", server.call(f"GetMessage/{q}")

    for name, payload in [("not_json", "not json"), ("array", "[1,2]"), ("string", '"x"'), ("truncated", '{"Id":"'), ("empty", "")]:
        yield f"invalid_{name}.publish", publish(queue(), payload)

    q = queue()
    yield "raw_slash.publish", server.call(f"UpdateMessage/{q}/%7B%22Id%22%3A%22%22%2C%22Body%22%3A%7B%22p%22%3A%22a/b/c%22%7D%2C%22RPC%22%3Afalse%2C%22TTL%22%3A0%7D")
    yield "raw_slash.get", server.call(f"GetMessage/{q}")

    q = queue()
    yield "plus_sign.publish", server.call(f"UpdateMessage/{q}/%7B%22Id%22%3A%22%22%2C%22Body%22%3A%7B%22p%22%3A%22a+b%20c%22%7D%2C%22RPC%22%3Afalse%2C%22TTL%22%3A0%7D")
    yield "plus_sign.get", server.call(f"GetMessage/{q}")

    q = queue()
    yield "question_mark.publish", server.call(f"UpdateMessage/{q}/%7B%22Id%22%3A%22%22%2C%22Body%22%3A%7B%22p%22%3A%22a?b=1%22%7D%2C%22RPC%22%3Afalse%2C%22TTL%22%3A0%7D")
    yield "question_mark.get", server.call(f"GetMessage/{q}")

    q = queue()
    yield "rpc.publish", publish(q, '{"Id":null,"Body":{"ask":1},"RPC":true,"TTL":0,"Response":null}')
    rpc_id = server.last_id or "{MISSING}"
    yield "rpc.response_before_delivery", server.call(f"GetRPCResponse/{q}/{enc(rpc_id)}")
    yield "rpc.get", server.call(f"GetMessage/{q}")
    yield "rpc.get_again", server.call(f"GetMessage/{q}")
    yield "rpc.response_before_answer", server.call(f"GetRPCResponse/{q}/{enc(rpc_id)}")
    yield "rpc.answer", server.call(f"UpdateRPCResponse/{q}/{enc(rpc_id)}/{enc(ANSWER)}")
    yield "rpc.response", server.call(f"GetRPCResponse/{q}/{enc(rpc_id)}")
    yield "rpc.response_again", server.call(f"GetRPCResponse/{q}/{enc(rpc_id)}")

    q = queue()
    yield "rpc_unbraced_id.publish", publish(q, '{"Id":"","Body":{},"RPC":true,"TTL":0}')
    rpc_id = server.last_id or "{MISSING}"
    yield "rpc_unbraced_id.get", server.call(f"GetMessage/{q}")
    yield "rpc_unbraced_id.answer_raw_braces", server.call(f"UpdateRPCResponse/{q}/{rpc_id}/%7B%22a%22%3A1%7D")
    yield "rpc_unbraced_id.response_raw_braces", server.call(f"GetRPCResponse/{q}/{rpc_id}")

    q = queue()
    yield "answer_pending.publish", publish(q, '{"Id":"","Body":{},"RPC":true,"TTL":0}')
    rpc_id = server.last_id or "{MISSING}"
    yield "answer_pending.answer", server.call(f"UpdateRPCResponse/{q}/{enc(rpc_id)}/{enc(EMPTY)}")
    yield "answer_pending.get", server.call(f"GetMessage/{q}")
    yield "answer_pending.response", server.call(f"GetRPCResponse/{q}/{enc(rpc_id)}")

    q = queue()
    yield "answer_plain.publish", publish(q, '{"Id":"","Body":{},"RPC":false,"TTL":0}')
    plain_id = server.last_id or "{MISSING}"
    yield "answer_plain.answer_pending", server.call(f"UpdateRPCResponse/{q}/{enc(plain_id)}/{enc(EMPTY)}")

    q = queue()
    yield "answer_invalid.publish", publish(q, '{"Id":"","Body":{},"RPC":true,"TTL":0}')
    rpc_id = server.last_id or "{MISSING}"
    yield "answer_invalid.get", server.call(f"GetMessage/{q}")
    yield "answer_invalid.not_json", server.call(f"UpdateRPCResponse/{q}/{enc(rpc_id)}/{enc(NOT_JSON)}")
    yield "answer_invalid.array", server.call(f"UpdateRPCResponse/{q}/{enc(rpc_id)}/{enc(ARRAY)}")
    yield "answer_invalid.twice_first", server.call(f"UpdateRPCResponse/{q}/{enc(rpc_id)}/{enc(N1)}")
    yield "answer_invalid.twice_second", server.call(f"UpdateRPCResponse/{q}/{enc(rpc_id)}/{enc(N2)}")
    yield "answer_invalid.response", server.call(f"GetRPCResponse/{q}/{enc(rpc_id)}")

    yield "answer.unknown_queue", server.call(f"UpdateRPCResponse/{queue()}/{enc(NO_ID)}/{enc(EMPTY)}")
    yield "response.unknown_queue", server.call(f"GetRPCResponse/{queue()}/{enc(NO_ID)}")

    q = queue()
    yield "ttl_short.publish", publish(q, '{"Id":"","Body":{},"RPC":false,"TTL":1500}')
    time.sleep(4)
    yield "ttl_short.get_after_4s", server.call(f"GetMessage/{q}")

    # 67536 = 65536 + 2000: a 16-bit TTL would wrap to 2 s.
    for name, ttl in [("40000", 40000), ("67536", 67536), ("negative", -1), ("text", '"5"'), ("fraction", 1500.5)]:
        q = queue()
        yield f"ttl_{name}.publish", publish(q, '{"Id":"","Body":{},"RPC":false,"TTL":' + str(ttl) + "}")
        time.sleep(4)
        yield f"ttl_{name}.get_after_4s", server.call(f"GetMessage/{q}")

    yield "errors.unknown_method", server.call(f"Nope/{queue()}")
    yield "errors.get_message_no_queue", server.call("GetMessage")
    yield "errors.get_message_empty_queue", server.call("GetMessage/")
    yield "errors.update_message_no_payload", server.call(f"UpdateMessage/{queue()}")
    yield "errors.get_message_extra_parameter", server.call(f"GetMessage/{queue()}/extra")
    yield "errors.lowercase_method", server.call(f"getmessage/{queue()}")

    q = queue()
    yield "verbs.post_update", server.call(f"UpdateMessage/{q}/{enc(POSTED)}", method="POST")
    yield "verbs.post_get", server.call(f"GetMessage/{q}", method="POST")
    yield "verbs.get_after_posts", server.call(f"GetMessage/{q}")


def normalise(exchange):
    """Drops what legitimately differs between two runs: ids, session numbers, dates."""
    headers = {k: v for k, v in exchange["headers"].items() if k not in VOLATILE_HEADERS}
    if "pragma" in headers:
        headers["pragma"] = re.sub(r"dssession=[^,]*", "dssession=<session>", headers["pragma"])
    return {"status": exchange["status"], "headers": headers, "body": ID.sub("{<id>}", exchange["body"])}


def record(base, out):
    server = Server(base)
    results = {}
    for name, exchange in scenarios(server):
        results[name] = normalise(exchange)
        print(f"{name:45} {exchange['status']} {exchange['body'][:110]}")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(results, handle, indent=2, ensure_ascii=False, sort_keys=True)
        handle.write("\n")


def meaning(body):
    """The body as the clients see it: the envelope parsed, and the inner message parsed too."""
    try:
        outer = json.loads(body)
    except ValueError:
        return body
    result = outer.get("result") if isinstance(outer, dict) else None
    if isinstance(result, list) and len(result) == 1 and isinstance(result[0], str):
        try:
            return {"result": [json.loads(result[0])]}
        except ValueError:
            pass
    return outer


def compare(reference_path, other_path):
    reference = json.load(open(reference_path, encoding="utf-8"))
    other = json.load(open(other_path, encoding="utf-8"))
    different = intentional = 0
    for name in sorted(reference):
        expected, actual = reference[name], other.get(name)
        notes = []
        if actual is None:
            notes.append("missing")
        else:
            if expected["status"] != actual["status"]:
                notes.append(f"status {expected['status']} -> {actual['status']}")
            if meaning(expected["body"]) != meaning(actual["body"]):
                notes.append(f"body\n      ref: {expected['body']}\n      got: {actual['body']}")
            elif expected["body"] != actual["body"]:
                notes.append(f"same meaning, different bytes\n      ref: {expected['body']}\n      got: {actual['body']}")
        if notes and name in INTENTIONAL:
            intentional += 1
        elif notes:
            different += 1
            print(f"- {name}: " + "; ".join(notes))
    equal = len(reference) - different - intentional
    print(f"\n{equal} equal, {intentional} different on purpose, {different} different, of {len(reference)}")
    return 1 if different else 0


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "record":
        record(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 4 and sys.argv[1] == "compare":
        sys.exit(compare(sys.argv[2], sys.argv[3]))
    else:
        sys.exit(__doc__)
