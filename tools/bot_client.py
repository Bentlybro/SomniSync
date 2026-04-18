#!/usr/bin/env python3
"""
Headless SomniSync bot client — speaks the full binary protocol so the server
genuinely can't tell one of these from a real Unity client.

Use it to load-test server-side scaling (physics tick budget, broadcast
bandwidth, message relay) and to validate deterministic NPC sync across many
"clients" without needing actual humans. Each bot:

  * Runs its own NTP-style clock sync (same 1 Hz ping + EMA as the real client),
    so every bot has an independent ServerTime estimate — clock-skew between
    bots surfaces in the CSV exactly like it does between real clients.
  * Sends MSG_PLAYER_POS at 10 Hz so the server's AOI filter treats it as a
    real participant (and so AOI load gets exercised).
  * Receives MSG_POSITION_VELOCITY, CTRL_RELEASE, CTRL_SPAWN, and writes one
    CSV row per observed NetworkObject per log tick — same schema as
    NetworkDebug.cs, so tools/netdbg_diff.py works unchanged against a mix
    of bot and real-client CSVs.

The first client in a room is asked to send SCENE_INIT. Bots won't — start a
real Unity client (or one Somnium client) first, then launch the bot swarm.
If a bot ends up as first-in-room it logs a warning and disconnects.

Usage:
  python tools/bot_client.py --count 20 --room bench \\
      --server ws://127.0.0.1:9090 --out-dir D:/botlogs

Then compare any two CSVs post-hoc:
  python tools/netdbg_diff.py D:/botlogs/bot0.csv D:/botlogs/bot1.csv
"""
from __future__ import annotations

import argparse
import asyncio
import csv
import math
import random
import signal
import socket
import struct
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

import websockets
from websockets.exceptions import ConnectionClosed

# ─── Protocol constants (must match server/src/protocol.rs) ─────────────────
MSG_INTERACTION_UPDATE = 0x03
MSG_VARIABLE_UPDATE    = 0x04
MSG_EVENT              = 0x06
MSG_ANIMATOR           = 0x07
MSG_SCALED_INTERACTION = 0x09
MSG_POSITION_VELOCITY  = 0x0B
MSG_PLAYER_POS         = 0x0C
MSG_PUSH_IMPULSE       = 0x0D
MSG_TIME_PING          = 0x0E
MSG_CONTROL            = 0xFF

CTRL_WELCOME           = 0x01
CTRL_CLAIM             = 0x05
CTRL_RELEASE           = 0x06
CTRL_CLAIM_REJECTED    = 0x07
CTRL_PHYSICS_READY     = 0x08
CTRL_SPAWN_REQUEST     = 0x0B
CTRL_SPAWN             = 0x0C
CTRL_DESPAWN           = 0x0D
CTRL_TIME_PONG         = 0x0E

BYTES_PER_ENTITY_VELOCITY = 28  # [u16 id][f32 x/y/z][i16 qx/qy/qz/qw][half vx/vy/vz]

# EMA alpha mirrors the Unity client (NetworkManager.Receiver.cs:OffsetEmaAlpha).
OFFSET_EMA_ALPHA = 0.35

# ─── Binary helpers ──────────────────────────────────────────────────────────
def read_u16(b: bytes, o: int) -> int:
    return struct.unpack_from("<H", b, o)[0]

def read_i16(b: bytes, o: int) -> int:
    return struct.unpack_from("<h", b, o)[0]

def read_u64(b: bytes, o: int) -> int:
    return struct.unpack_from("<Q", b, o)[0]

def read_f32(b: bytes, o: int) -> float:
    return struct.unpack_from("<f", b, o)[0]

def read_f16(b: bytes, o: int) -> float:
    # struct 'e' = IEEE 754 half-precision (Python 3.6+).
    return struct.unpack_from("<e", b, o)[0]


# ─── Outgoing-message builders ───────────────────────────────────────────────
def make_player_pos(x: float, y: float, z: float, radius: float = 0.5, half_h: float = 1.0) -> bytes:
    buf = bytearray(21)
    buf[0] = MSG_PLAYER_POS
    struct.pack_into("<fffff", buf, 1, x, y, z, radius, half_h)
    return bytes(buf)


def make_time_ping(client_local_ms: int) -> bytes:
    buf = bytearray(9)
    buf[0] = MSG_TIME_PING
    struct.pack_into("<Q", buf, 1, client_local_ms)
    return bytes(buf)


def make_claim(obj_id: int, sender_cid: int) -> bytes:
    # [0xFF][0x05][u16 obj_id][u16 sender_cid]
    buf = bytearray(6)
    buf[0] = MSG_CONTROL
    buf[1] = CTRL_CLAIM
    struct.pack_into("<HH", buf, 2, obj_id, sender_cid)
    return bytes(buf)


def make_release(obj_id: int, sender_cid: int) -> bytes:
    # [0xFF][0x06][u16 obj_id][u16 sender_cid]
    buf = bytearray(6)
    buf[0] = MSG_CONTROL
    buf[1] = CTRL_RELEASE
    struct.pack_into("<HH", buf, 2, obj_id, sender_cid)
    return bytes(buf)


def make_interaction_update_single(obj_id: int, x: float, y: float, z: float) -> bytes:
    # [0x03][u16 count=1][u16 id][f32 x][f32 y][f32 z][i16 qx][i16 qy][i16 qz][i16 qw]
    # Quaternion identity quantised: (0, 0, 0, 32767).
    buf = bytearray(25)
    buf[0] = MSG_INTERACTION_UPDATE
    struct.pack_into("<H", buf, 1, 1)          # count
    struct.pack_into("<H", buf, 3, obj_id)     # id
    struct.pack_into("<fff", buf, 5, x, y, z)  # pos
    struct.pack_into("<hhhh", buf, 17, 0, 0, 0, 32767)  # quantised identity quat
    return bytes(buf)


# ─── Bot ────────────────────────────────────────────────────────────────────
@dataclass
class BotConfig:
    server: str
    room: str
    out_dir: Path
    tag: str
    bot_idx: int
    total_bots: int
    wander_radius: float = 2.0
    spawn_ring_radius: float = 8.0
    player_pos_hz: float = 10.0
    ping_hz: float = 1.0
    log_interval_s: float = 1.0
    # Physics-stress behaviour. When `interact` is on, each bot periodically
    # claims an object it can see and drives it through a small circular
    # motion at `interaction_hz` updates per second — this is what actually
    # feeds work into the server's Rapier kinematic body update path and
    # generates a broadcast fan-out. Leave off for pure observer load-tests.
    interact: bool = False
    claim_cooldown_s: float = 3.0   # wait this long between hold cycles
    hold_duration_s: float = 2.0    # drive each held object for this long
    interaction_hz: float = 60.0    # send rate while holding


class Bot:
    """
    One simulated client. Connects, keeps itself alive, logs observed state.

    Lifecycle:
      run() → connect → (receive loop, send_player_pos loop, ping loop, log loop)
      all loops share self._alive; cancellation cascades.
    """

    def __init__(self, cfg: BotConfig):
        self.cfg = cfg
        self._alive = True
        self._ws: Optional[websockets.WebSocketClientProtocol] = None
        self._t0 = time.perf_counter()

        # Clock sync state (per-bot NTP EMA, mirrors Unity client).
        self._offset_sec = 0.0
        self._best_rtt_sec = float("inf")
        self._time_sync_initialised = False

        # Network state from welcome / physics-ready.
        self._client_id = 0
        self._ready = False
        self._physics_ready = False
        self._needs_scene_init = False
        self._scene_ready_ms = 0

        # Latest observed position per object_id — written to CSV on log tick.
        self._observed: dict[int, tuple[float, float, float]] = {}
        # Latest release pose per object_id (for CSV lrp_* columns).
        self._last_release: dict[int, tuple[tuple[float, float, float], int]] = {}

        # Claim/release cycle state for physics-stress mode.
        self._held_obj_id: Optional[int] = None
        self._held_since: float = 0.0          # perf_counter when we sent CLAIM
        self._held_anchor: tuple[float, float, float] = (0.0, 0.0, 0.0)
        self._rng = random.Random(cfg.bot_idx * 1_000_003 + 42)

        # Deterministic unique position around a ring so bots don't all overlap
        # at origin. Phase by bot_idx → every bot sits at a known spot.
        angle = 2.0 * math.pi * self.cfg.bot_idx / max(1, self.cfg.total_bots)
        self._center = (
            self.cfg.spawn_ring_radius * math.cos(angle),
            0.0,
            self.cfg.spawn_ring_radius * math.sin(angle),
        )

        # CSV writer — opened on run(), closed in finally.
        self._csv_path = self.cfg.out_dir / f"netdbg_{self.cfg.tag}.csv"
        self._csv_file = None
        self._csv = None

    # ── Public ────────────────────────────────────────────────────────────
    async def run(self) -> None:
        self.cfg.out_dir.mkdir(parents=True, exist_ok=True)
        self._csv_file = self._csv_path.open("w", newline="", encoding="utf-8")
        self._csv = csv.writer(self._csv_file)
        self._csv.writerow([
            "tag", "localT", "serverT", "offset", "bestRTT", "cid", "ready", "physicsReady",
            "npc_id", "npc_name", "pos_x", "pos_y", "pos_z",
            "origin_x", "origin_y", "origin_z", "wanderEpoch", "wT", "anchoredMs",
            "lrp_x", "lrp_y", "lrp_z", "lrm", "claimLoc", "claimRem",
        ])
        self._csv_file.flush()

        uri = f"{self.cfg.server.rstrip('/')}/{self.cfg.room}"
        try:
            self._ws = await websockets.connect(uri, max_size=2**20, close_timeout=5)
            # Match server/client TCP_NODELAY so small ticks aren't coalesced.
            try:
                sock = self._ws.transport.get_extra_info("socket")
                if sock:
                    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            except Exception:
                pass

            loops = [
                self._recv_loop(),
                self._player_pos_loop(),
                self._time_ping_loop(),
                self._csv_log_loop(),
            ]
            if self.cfg.interact:
                loops.append(self._interaction_loop())
            await asyncio.gather(*loops, return_exceptions=False)
        except (ConnectionClosed, asyncio.CancelledError):
            pass
        except OSError as e:
            print(f"[{self.cfg.tag}] connect failed: {e}", file=sys.stderr)
        finally:
            self._alive = False
            if self._ws is not None:
                try:
                    await self._ws.close()
                except Exception:
                    pass
            if self._csv_file is not None:
                self._csv_file.close()

    def stop(self) -> None:
        self._alive = False

    # ── Loops ─────────────────────────────────────────────────────────────
    async def _recv_loop(self) -> None:
        assert self._ws is not None
        while self._alive:
            try:
                raw = await self._ws.recv()
            except ConnectionClosed:
                self._alive = False
                return
            if isinstance(raw, (bytes, bytearray)):
                self._dispatch(bytes(raw))

    async def _player_pos_loop(self) -> None:
        assert self._ws is not None
        interval = 1.0 / max(0.1, self.cfg.player_pos_hz)
        while self._alive:
            if self._ready:
                # Small circular wander around the bot's assigned ring position.
                # Gives the server's AOI filter something to chew on without
                # all bots sitting perfectly still.
                t = time.perf_counter() - self._t0
                w = 2.0 * math.pi / 12.0  # 12s orbit
                x = self._center[0] + self.cfg.wander_radius * math.cos(w * t + self.cfg.bot_idx)
                z = self._center[2] + self.cfg.wander_radius * math.sin(w * t + self.cfg.bot_idx)
                y = self._center[1] + 1.0  # eye height-ish
                try:
                    await self._ws.send(make_player_pos(x, y, z))
                except ConnectionClosed:
                    self._alive = False
                    return
            await asyncio.sleep(interval)

    async def _time_ping_loop(self) -> None:
        assert self._ws is not None
        interval = 1.0 / max(0.1, self.cfg.ping_hz)
        while self._alive:
            if self._ready:
                client_ms = int((time.perf_counter() - self._t0) * 1000.0)
                try:
                    await self._ws.send(make_time_ping(client_ms))
                except ConnectionClosed:
                    self._alive = False
                    return
            await asyncio.sleep(interval)

    async def _csv_log_loop(self) -> None:
        while self._alive:
            await asyncio.sleep(self.cfg.log_interval_s)
            if not self._ready:
                continue
            self._flush_csv_tick()

    async def _interaction_loop(self) -> None:
        """
        Physics-stress behaviour. Life cycle:
          1. Wait for at least one observable object + cooldown elapsed.
          2. Pick one at random, cache its pose, send CTRL_CLAIM.
          3. Drive it in a small circle at interaction_hz for hold_duration_s.
             Server treats each MSG_INTERACTION_UPDATE as a kinematic-body
             position write into Rapier + relays to other clients — this is
             the hot path we actually want to stress.
          4. Send CTRL_RELEASE. Return to (1) after claim_cooldown_s.

        If a claim gets rejected (CTRL_CLAIM_REJECTED from the server because
        someone else already holds it), abort this cycle and try a different
        object next round. Don't spam the same rejected obj_id.
        """
        assert self._ws is not None
        interval = 1.0 / max(1.0, self.cfg.interaction_hz)
        # Small per-bot offset so 40 bots don't hit the server in lockstep.
        await asyncio.sleep(self._rng.uniform(0.1, self.cfg.claim_cooldown_s))

        while self._alive:
            if not self._physics_ready or not self._observed:
                await asyncio.sleep(0.5)
                continue

            # Pick a target that isn't one we know we can't hold. We don't
            # track "known-bad" long-term — CLAIM_REJECTED just aborts the
            # current cycle and we'll randomly reselect on the next loop.
            target_id = self._rng.choice(list(self._observed.keys()))
            anchor = self._observed[target_id]
            self._held_obj_id = target_id
            self._held_anchor = anchor
            self._held_since = time.perf_counter()

            try:
                await self._ws.send(make_claim(target_id, self._client_id))
            except ConnectionClosed:
                self._alive = False
                return

            # Drive at interaction_hz until either the hold duration elapses
            # or a CLAIM_REJECTED invalidates our hold.
            drive_start = time.perf_counter()
            w = 2.0 * math.pi / 0.9  # 0.9s rotation period — tight, lots of motion
            r = 0.3
            last_send = drive_start
            while self._alive and self._held_obj_id == target_id:
                now = time.perf_counter()
                if now - drive_start >= self.cfg.hold_duration_s:
                    break
                if now - last_send >= interval:
                    t = now - drive_start
                    x = anchor[0] + r * math.cos(w * t)
                    y = anchor[1]
                    z = anchor[2] + r * math.sin(w * t)
                    try:
                        await self._ws.send(
                            make_interaction_update_single(target_id, x, y, z)
                        )
                    except ConnectionClosed:
                        self._alive = False
                        return
                    last_send = now
                else:
                    await asyncio.sleep(max(0.0, interval - (now - last_send)))

            # Always attempt release — even if the hold was rejected/aborted,
            # releasing an object we don't own is harmless (server ignores
            # claim-not-owner). Belt-and-suspenders against state desync.
            if self._held_obj_id == target_id:
                try:
                    await self._ws.send(make_release(target_id, self._client_id))
                except ConnectionClosed:
                    self._alive = False
                    return
            self._held_obj_id = None
            await asyncio.sleep(self.cfg.claim_cooldown_s)

    # ── Dispatch ──────────────────────────────────────────────────────────
    def _dispatch(self, data: bytes) -> None:
        if len(data) == 0:
            return
        mtype = data[0]
        if mtype == MSG_POSITION_VELOCITY:
            self._handle_pos_velocity(data)
        elif mtype == MSG_CONTROL and len(data) >= 2:
            ctrl = data[1]
            if ctrl == CTRL_WELCOME:
                self._handle_welcome(data)
            elif ctrl == CTRL_PHYSICS_READY:
                self._handle_physics_ready(data)
            elif ctrl == CTRL_TIME_PONG:
                self._handle_time_pong(data)
            elif ctrl == CTRL_SPAWN:
                self._handle_spawn(data)
            elif ctrl == CTRL_DESPAWN:
                self._handle_despawn(data)
            elif ctrl == CTRL_RELEASE:
                self._handle_release(data)
            elif ctrl == CTRL_CLAIM_REJECTED:
                self._handle_claim_rejected(data)
            # Ignore other control messages (CTRL_CLAIM broadcasts for other
            # clients don't matter to us).

    # ── Handlers ──────────────────────────────────────────────────────────
    def _handle_welcome(self, data: bytes) -> None:
        if len(data) < 5:
            return
        self._client_id = read_u16(data, 2)
        self._needs_scene_init = data[4] != 0
        if len(data) >= 13:
            server_ms = read_u64(data, 5)
            # Seed the offset before ping/pong refines it, same as Unity client.
            local_sec = time.perf_counter() - self._t0
            server_sec = server_ms / 1000.0
            self._offset_sec = local_sec - server_sec
        if len(data) >= 21:
            scene_ready_ms = read_u64(data, 13)
            if scene_ready_ms != 0:
                self._scene_ready_ms = scene_ready_ms
        self._ready = True

        if self._needs_scene_init:
            # Bots can't describe the scene — a real Unity client must bootstrap
            # the physics world first. Disconnect so we don't leave the room in a
            # broken state waiting for a SCENE_INIT that will never arrive.
            print(
                f"[{self.cfg.tag}] first in room (needs_scene_init=1) — disconnecting. "
                f"Start a real client in '{self.cfg.room}' before launching bots.",
                file=sys.stderr,
            )
            self._alive = False

    def _handle_physics_ready(self, data: bytes) -> None:
        self._physics_ready = True
        if len(data) >= 10:
            v = read_u64(data, 2)
            if v != 0:
                self._scene_ready_ms = v

    def _handle_time_pong(self, data: bytes) -> None:
        if len(data) < 18:
            return
        client_sent_ms = read_u64(data, 2)
        server_ms = read_u64(data, 10)
        local_now_ms = (time.perf_counter() - self._t0) * 1000.0
        rtt_ms = local_now_ms - client_sent_ms
        if rtt_ms < 0 or rtt_ms > 2000:
            return
        rtt_sec = rtt_ms / 1000.0
        if rtt_sec < self._best_rtt_sec:
            self._best_rtt_sec = rtt_sec
        estimated_server_ms_now = server_ms + rtt_ms * 0.5
        sample_offset_sec = (local_now_ms - estimated_server_ms_now) / 1000.0
        if not self._time_sync_initialised:
            self._offset_sec = sample_offset_sec
            self._time_sync_initialised = True
        else:
            self._offset_sec = (
                self._offset_sec * (1.0 - OFFSET_EMA_ALPHA)
                + sample_offset_sec * OFFSET_EMA_ALPHA
            )

    def _handle_pos_velocity(self, data: bytes) -> None:
        # [0x0B][u16 sender][u16 seq][u16 count][N × 28B entity]
        if len(data) < 7:
            return
        count = read_u16(data, 5)
        off = 7
        for _ in range(count):
            if off + BYTES_PER_ENTITY_VELOCITY > len(data):
                break
            obj_id = read_u16(data, off)
            px = read_f32(data, off + 2)
            py = read_f32(data, off + 6)
            pz = read_f32(data, off + 10)
            # Skip rot (i16×4 = 8 bytes) and vel (half×3 = 6 bytes) — we don't
            # diff those and they'd just bloat every row.
            self._observed[obj_id] = (px, py, pz)
            off += BYTES_PER_ENTITY_VELOCITY

    def _handle_spawn(self, data: bytes) -> None:
        # [0xFF][0x0C][u16 id][u8 prefab][60B scene_object][u64 spawn_server_ms]
        if len(data) < 5 + 60:
            return
        obj_id = read_u16(data, 2)
        # Initial spawn position is at offset 5 + 4 = 9 (skip id+body+shape in obj body).
        px = read_f32(data, 5 + 4)
        py = read_f32(data, 5 + 8)
        pz = read_f32(data, 5 + 12)
        self._observed[obj_id] = (px, py, pz)

    def _handle_claim_rejected(self, data: bytes) -> None:
        # [0xFF][0x07][u16 obj_id]
        if len(data) < 4:
            return
        obj_id = read_u16(data, 2)
        # If the rejection is for the object we thought we were driving,
        # clear our held state so the interaction loop stops sending
        # updates the server will just ignore.
        if self._held_obj_id == obj_id:
            self._held_obj_id = None

    def _handle_despawn(self, data: bytes) -> None:
        # [0xFF][0x0D][u16 id]
        if len(data) < 4:
            return
        obj_id = read_u16(data, 2)
        self._observed.pop(obj_id, None)
        self._last_release.pop(obj_id, None)

    def _handle_release(self, data: bytes) -> None:
        if len(data) < 34:
            return
        obj_id = read_u16(data, 2)
        px = read_f32(data, 6)
        py = read_f32(data, 10)
        pz = read_f32(data, 14)
        server_ms = read_u64(data, 26)
        self._last_release[obj_id] = ((px, py, pz), server_ms)
        # Also update the observed-position cache so a stationary released
        # object shows up in the CSV even when the physics broadcast skips it.
        self._observed[obj_id] = (px, py, pz)

    # ── CSV output ────────────────────────────────────────────────────────
    def _server_time_sec(self) -> float:
        local_sec = time.perf_counter() - self._t0
        return local_sec - self._offset_sec if self._ready else 0.0

    def _flush_csv_tick(self) -> None:
        if self._csv is None or self._csv_file is None:
            return
        local_t = time.perf_counter() - self._t0
        server_t = self._server_time_sec()
        offset = self._offset_sec
        best_rtt = self._best_rtt_sec if self._best_rtt_sec != float("inf") else -1.0
        ready = 1 if self._ready else 0
        phys = 1 if self._physics_ready else 0
        cid = self._client_id
        tag = self.cfg.tag

        if not self._observed:
            # Emit a clock-only row so uptime-vs-offset is still recoverable.
            self._csv.writerow([
                tag, f"{local_t:.3f}", f"{server_t:.6f}", f"{offset:.6f}",
                f"{best_rtt:.6f}", cid, ready, phys,
                "", "", "0.0000", "0.0000", "0.0000",
                "0.0000", "0.0000", "0.0000", "0.000000", "0.000000", 0,
                "0.0000", "0.0000", "0.0000", 0, 0, 0,
            ])
        else:
            for obj_id, (x, y, z) in self._observed.items():
                lrp, lrm = self._last_release.get(obj_id, ((0.0, 0.0, 0.0), 0))
                self._csv.writerow([
                    tag, f"{local_t:.3f}", f"{server_t:.6f}", f"{offset:.6f}",
                    f"{best_rtt:.6f}", cid, ready, phys,
                    obj_id, "", f"{x:.4f}", f"{y:.4f}", f"{z:.4f}",
                    "0.0000", "0.0000", "0.0000", "0.000000", "0.000000", 0,
                    f"{lrp[0]:.4f}", f"{lrp[1]:.4f}", f"{lrp[2]:.4f}", lrm, 0, 0,
                ])
        self._csv_file.flush()


# ─── Orchestration ──────────────────────────────────────────────────────────
async def run_swarm(args: argparse.Namespace) -> None:
    out_dir = Path(args.out_dir)
    bots = [
        Bot(BotConfig(
            server=args.server,
            room=args.room,
            out_dir=out_dir,
            tag=f"bot{i:02d}",
            bot_idx=i,
            total_bots=args.count,
            wander_radius=args.wander_radius,
            spawn_ring_radius=args.ring_radius,
            player_pos_hz=args.player_pos_hz,
            ping_hz=args.ping_hz,
            log_interval_s=args.log_interval,
            interact=args.interact,
            claim_cooldown_s=args.claim_cooldown,
            hold_duration_s=args.hold_duration,
            interaction_hz=args.interaction_hz,
        ))
        for i in range(args.count)
    ]

    stop_event = asyncio.Event()

    def request_stop() -> None:
        for b in bots:
            b.stop()
        stop_event.set()

    loop = asyncio.get_running_loop()
    # Clean-shutdown on SIGINT/SIGTERM. Windows' ProactorEventLoop doesn't
    # support add_signal_handler — fall through, Ctrl-C still works via the
    # default KeyboardInterrupt path in main().
    try:
        loop.add_signal_handler(signal.SIGINT, request_stop)
        loop.add_signal_handler(signal.SIGTERM, request_stop)
    except (NotImplementedError, AttributeError):
        pass

    print(f"[swarm] launching {len(bots)} bot(s) → {args.server}/{args.room}")
    print(f"[swarm] CSV output: {out_dir}")
    tasks = [asyncio.create_task(b.run()) for b in bots]

    duration = args.duration
    if duration > 0:
        try:
            await asyncio.wait_for(stop_event.wait(), timeout=duration)
        except asyncio.TimeoutError:
            pass
        request_stop()

    await asyncio.gather(*tasks, return_exceptions=True)
    print(f"[swarm] done. {len(bots)} CSV files written to {out_dir}")


def main() -> int:
    p = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("--server", default="ws://127.0.0.1:9090", help="WebSocket server URL (no trailing /room).")
    p.add_argument("--room", default="bench", help="Room name. Must match the real client's room.")
    p.add_argument("--count", type=int, default=20, help="Number of bots.")
    p.add_argument("--duration", type=float, default=120.0, help="Seconds to run before shutting down. 0 = forever.")
    p.add_argument("--out-dir", default="./botlogs", help="Directory for per-bot CSVs.")
    p.add_argument("--ring-radius", type=float, default=8.0, help="Radius of the ring bots spawn on (metres).")
    p.add_argument("--wander-radius", type=float, default=2.0, help="Per-bot wander radius around its ring slot.")
    p.add_argument("--player-pos-hz", type=float, default=10.0, help="MSG_PLAYER_POS send rate.")
    p.add_argument("--ping-hz", type=float, default=1.0, help="MSG_TIME_PING rate.")
    p.add_argument("--log-interval", type=float, default=1.0, help="Seconds between CSV log ticks per bot.")

    g = p.add_argument_group("physics stress (--interact)")
    g.add_argument("--interact", action="store_true",
                   help="Bots periodically claim an object and drive it via MSG_INTERACTION_UPDATE. "
                        "This is the only mode that actually stresses the server's Rapier write path "
                        "and the held-object relay; without it you're only testing AOI + broadcast throughput.")
    g.add_argument("--claim-cooldown", type=float, default=3.0, help="Seconds between hold cycles.")
    g.add_argument("--hold-duration", type=float, default=2.0, help="Seconds each hold cycle lasts.")
    g.add_argument("--interaction-hz", type=float, default=60.0, help="MSG_INTERACTION_UPDATE rate while holding.")

    args = p.parse_args()

    try:
        asyncio.run(run_swarm(args))
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
