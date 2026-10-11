"""Check room writer election and late-join variable replay on a running relay.

Requires `websockets`. Uses a unique temporary room and leaves no server state
once every client disconnects.
"""

import argparse
import asyncio
import struct
import time

import websockets

from load_scene import scene_init


def variable_update(value: int, persistent: bool = True) -> bytes:
    name = b"hp"
    flags = 0x80 if persistent else 0
    return b"\x04" + struct.pack("<HBB", 0, 1, len(name)) + name + bytes([flags]) + struct.pack("<H", 4) + struct.pack("<i", value)


async def until(socket, predicate, timeout=5):
    async def read():
        while True:
            frame = await socket.recv()
            if isinstance(frame, bytes) and predicate(frame):
                return frame

    return await asyncio.wait_for(read(), timeout)


async def connect(url):
    socket = await websockets.connect(url, max_size=None, compression=None)
    welcome = await until(socket, lambda m: m[:2] == b"\xff\x01")
    writer = await until(socket, lambda m: m[:2] == b"\xff\x03")
    return socket, struct.unpack_from("<H", welcome, 2)[0], struct.unpack_from("<H", writer, 2)[0]


async def run(url):
    room_url = url.rstrip("/") + "/statecheck-" + str(time.time_ns()) + "?scene=0000000000000001"
    sockets = []
    try:
        first, first_id, first_writer = await connect(room_url)
        sockets.append(first)
        assert first_id == first_writer
        await first.send(scene_init(1))
        await until(first, lambda m: m[:2] == b"\xff\x08")

        incompatible_url = room_url.rsplit("=", 1)[0] + "=0000000000000002"
        incompatible = await websockets.connect(incompatible_url, max_size=None, compression=None)
        try:
            rejected = await until(incompatible, lambda m: m[:2] == b"\xff\xfe")
            assert rejected == b"\xff\xfe\x02", "different scene was not rejected"
        finally:
            await incompatible.close()

        second, second_id, writer = await connect(room_url)
        sockets.append(second)
        assert writer == first_id
        event = b"\x06\x00\x00\x00\x04ping\x01\x00\x04\x00\x05\x00\x00\x00"
        await first.send(event)
        assert await until(second, lambda m: m[:1] == b"\x06") == event
        malformed_event = bytearray(event)
        malformed_event[3] = 3  # unknown target
        await first.send(bytes(malformed_event))
        try:
            await until(second, lambda m: m[:1] == b"\x06", timeout=0.3)
            raise AssertionError("malformed event was relayed")
        except asyncio.TimeoutError:
            pass
        await first.send(variable_update(5))
        assert await until(second, lambda m: m[:1] == b"\x04") == variable_update(5)

        # A nonwriter must not overwrite an unclaimed object's shared state.
        await second.send(variable_update(99))
        try:
            await until(first, lambda m: m[:1] == b"\x04", timeout=0.3)
            raise AssertionError("nonwriter variable update was relayed")
        except asyncio.TimeoutError:
            pass

        third, third_id, writer = await connect(room_url)
        sockets.append(third)
        assert writer == first_id
        snapshot = await until(third, lambda m: m[:1] == b"\x12")
        assert struct.unpack_from("<H", snapshot, 1)[0] == 1
        assert snapshot.endswith(struct.pack("<i", 5))

        await first.close()
        writer_change = await until(second, lambda m: m[:2] == b"\xff\x03")
        assert struct.unpack_from("<H", writer_change, 2)[0] == second_id
        await second.send(variable_update(4))
        assert await until(third, lambda m: m[:1] == b"\x04") == variable_update(4)

        fourth, _, writer = await connect(room_url)
        sockets.append(fourth)
        assert writer == second_id
        snapshot = await until(fourth, lambda m: m[:1] == b"\x12")
        assert snapshot.endswith(struct.pack("<i", 4))
        print("PASS: scene mismatch, event validation, elected writer, rejected nonwriter, late-join replay, writer handoff")
    finally:
        await asyncio.gather(*(socket.close() for socket in sockets), return_exceptions=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="ws://127.0.0.1:9090")
    args = parser.parse_args()
    asyncio.run(run(args.url))
