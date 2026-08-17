import { DurableObject } from "cloudflare:workers";

const ProtocolVersion = 1;
const MaxPlayers = 16;
const MaxDeckBytes = 100 * 1024 * 1024;
const RoomLifetimeMs = 6 * 60 * 60 * 1000;
const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

interface Env {
  ROOMS: DurableObjectNamespace<RelayRoom>;
  DIRECTORY: DurableObjectNamespace<RoomDirectory>;
  DECKS: R2Bucket;
}

type Visibility = "public" | "private";
interface RoomSummary {
  code: string;
  name: string;
  host: string;
  visibility: Visibility;
  intensity: string[];
  passwordProtected: boolean;
  players: number;
  capacity: number;
  started: boolean;
  expiresAt: number;
}

interface RoomState extends RoomSummary {
  hostToken: string;
  passwordSalt?: string;
  passwordHash?: string;
  deckKey?: string;
  deckHash?: string;
  deckBytes?: number;
  game?: GameState;
}

interface PendingVolunteer {
  resolutionId: string;
  cardId: string;
  drawer: string;
  deadline: number;
}

interface GameState {
  configuredCards: string[];
  keywords: Record<string, string>;
  drawPile: string[];
  turnOrder: string[];
  turnIndex: number;
  started: boolean;
  scoringEnabled: boolean;
  scores: Record<string, number>;
  scoringDrawer: string;
  eligibleVoters: string[];
  votes: Record<string, number>;
  currentCardId?: string;
  currentDrawer?: string;
  randomTarget?: string;
  pendingVolunteer?: PendingVolunteer;
  tieBreakCandidates: string[];
  eligibleTieVoters: string[];
  tieVotes: Record<string, string>;
}

interface Seat {
  id: string;
  name: string;
  token: string;
  reconnectToken: string;
  host: boolean;
  connected: boolean;
}

interface SocketAttachment { seatId: string; }

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), {
  status,
  headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" },
});
const error = (message: string, status = 400) => json({ error: message }, status);
const bearer = (request: Request) => request.headers.get("authorization")?.replace(/^Bearer\s+/i, "") ?? "";
const randomToken = (bytes = 24) => {
  const data = crypto.getRandomValues(new Uint8Array(bytes));
  return btoa(String.fromCharCode(...data)).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
};
const randomCode = () => Array.from(crypto.getRandomValues(new Uint8Array(8)), value => alphabet[value % alphabet.length]).join("");
const sha256 = async (value: string | ArrayBuffer) => {
  const bytes = typeof value === "string" ? new TextEncoder().encode(value) : value;
  return [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))].map(x => x.toString(16).padStart(2, "0")).join("");
};
const cleanText = (value: unknown, maximum: number) => typeof value === "string" ? value.trim().slice(0, maximum) : "";

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (request.method === "GET" && url.pathname === "/health")
      return json({ service: "levemetes-relay", status: "ok", protocol: ProtocolVersion });

    if (request.method === "GET" && url.pathname === "/api/v1/rooms")
      return env.DIRECTORY.getByName("global").fetch("https://directory/list");

    if (request.method === "POST" && url.pathname === "/api/v1/rooms") {
      const input = await request.json<Record<string, unknown>>().catch(() => null);
      if (!input) return error("Invalid room request.");
      const code = randomCode();
      return env.ROOMS.getByName(code).fetch("https://room/create", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ ...input, code }),
      });
    }

    const match = url.pathname.match(/^\/api\/v1\/rooms\/([A-Z2-9]{8})(?:\/(join|socket|deck))?$/i);
    if (!match) return error("Not found.", 404);
    const code = match[1].toUpperCase();
    const action = match[2] ?? "details";
    const target = new URL(request.url);
    target.protocol = "https:";
    target.hostname = "room";
    target.pathname = `/${action}`;
    return env.ROOMS.getByName(code).fetch(new Request(target, request));
  },
};

export class RoomDirectory extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.ctx.storage.sql.exec(`CREATE TABLE IF NOT EXISTS rooms
      (code TEXT PRIMARY KEY,name TEXT,host TEXT,visibility TEXT,intensity TEXT,passwordProtected INTEGER,players INTEGER,capacity INTEGER,started INTEGER,expiresAt INTEGER)`);
  }

  async fetch(request: Request): Promise<Response> {
    const path = new URL(request.url).pathname;
    if (path === "/list") {
      const rows = this.ctx.storage.sql.exec<Record<string, SqlStorageValue>>(
        "SELECT code,name,host,visibility,intensity,passwordProtected,players,capacity,started,expiresAt FROM rooms WHERE visibility='public' AND expiresAt>? ORDER BY name LIMIT 200",
        Date.now()).toArray();
      return json(rows.map(row => ({ ...row, intensity: JSON.parse(String(row.intensity)),
        passwordProtected: !!row.passwordProtected, started: !!row.started })));
    }
    if (path === "/upsert") {
      const room = await request.json<RoomSummary>();
      this.ctx.storage.sql.exec(`INSERT OR REPLACE INTO rooms VALUES(?,?,?,?,?,?,?,?,?,?)`, room.code, room.name,
        room.host, room.visibility, JSON.stringify(room.intensity), room.passwordProtected ? 1 : 0, room.players,
        room.capacity, room.started ? 1 : 0, room.expiresAt);
      return new Response(null, { status: 204 });
    }
    if (path === "/remove") {
      const { code } = await request.json<{ code: string }>();
      this.ctx.storage.sql.exec("DELETE FROM rooms WHERE code=?", code);
      return new Response(null, { status: 204 });
    }
    return error("Not found.", 404);
  }
}

export class RelayRoom extends DurableObject<Env> {
  private state?: RoomState;
  private seats = new Map<string, Seat>();

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.ctx.blockConcurrencyWhile(async () => {
      this.state = await this.ctx.storage.get<RoomState>("state");
      const saved = await this.ctx.storage.get<Seat[]>("seats") ?? [];
      this.seats = new Map(saved.map(seat => [seat.id, { ...seat, connected: false }]));
      for (const socket of this.ctx.getWebSockets()) {
        const attachment = socket.deserializeAttachment() as SocketAttachment | null;
        if (attachment && this.seats.has(attachment.seatId)) this.seats.get(attachment.seatId)!.connected = true;
      }
    });
  }

  async fetch(request: Request): Promise<Response> {
    const path = new URL(request.url).pathname;
    if (path === "/create" && request.method === "POST") return this.create(request);
    if (!this.state || this.state.expiresAt <= Date.now()) return error("Room not found or expired.", 404);
    if (path === "/details") return json(this.publicState());
    if (path === "/join" && request.method === "POST") return this.join(request);
    if (path === "/socket") return this.socket(request);
    if (path === "/deck" && request.method === "PUT") return this.uploadDeck(request);
    if (path === "/deck" && request.method === "GET") return this.downloadDeck(request);
    return error("Not found.", 404);
  }

  private async create(request: Request): Promise<Response> {
    if (this.state) return error("Room already exists.", 409);
    const input = await request.json<Record<string, unknown>>();
    const name = cleanText(input.name, 80);
    const host = cleanText(input.host, 100);
    if (!name || !host) return error("Room name and host character are required.");
    const visibility: Visibility = input.visibility === "private" ? "private" : "public";
    const password = cleanText(input.password, 128);
    const intensity = Array.isArray(input.intensity)
      ? input.intensity.map(value => cleanText(value, 12)).filter(Boolean).slice(0, 4) : [];
    if (!intensity.length) return error("Select at least one intensity category.");
    const hostToken = randomToken();
    const hostSeat = this.newSeat(host, true);
    const expiresAt = Date.now() + RoomLifetimeMs;
    this.state = {
      code: cleanText(input.code, 8), name, host, visibility, intensity,
      passwordProtected: !!password, players: 1, capacity: MaxPlayers, started: false, expiresAt, hostToken,
    };
    if (password) {
      this.state.passwordSalt = randomToken(12);
      this.state.passwordHash = await sha256(`${this.state.passwordSalt}:${password}`);
    }
    this.seats.set(hostSeat.id, hostSeat);
    await this.persist();
    await this.ctx.storage.setAlarm(expiresAt);
    await this.publish();
    return json({ room: this.publicState(), hostToken, seatToken: hostSeat.token, reconnectToken: hostSeat.reconnectToken }, 201);
  }

  private async join(request: Request): Promise<Response> {
    const input: Record<string, unknown> = await request.json<Record<string, unknown>>()
      .catch(() => ({} as Record<string, unknown>));
    const reconnect = cleanText(input.reconnectToken, 128);
    if (reconnect) {
      const existing = [...this.seats.values()].find(seat => seat.reconnectToken === reconnect);
      if (!existing) return error("That reconnect token is invalid.", 401);
      existing.token = randomToken();
      await this.persist();
      return json({ room: this.publicState(), seatToken: existing.token, reconnectToken: existing.reconnectToken,
        hostToken: existing.host ? this.state!.hostToken : undefined, reconnected: true });
    }
    if (!this.state!.deckKey) return error("The host is still preparing the synchronized deck. Try again shortly.", 409);
    if (this.seats.size >= MaxPlayers) return error("This room is full.", 409);
    if (this.state!.started) return error("This game has already started. Only seated players may reconnect.", 409);
    const name = cleanText(input.name, 100);
    if (!name) return error("Character name and world are required.");
    if ([...this.seats.values()].some(seat => seat.name.toLowerCase() === name.toLowerCase()))
      return error("That character already has a seat in this room.", 409);
    if (this.state!.passwordHash) {
      const supplied = await sha256(`${this.state!.passwordSalt}:${cleanText(input.password, 128)}`);
      if (supplied !== this.state!.passwordHash) return error("Incorrect room password.", 401);
    }
    const seat = this.newSeat(name, false);
    this.seats.set(seat.id, seat);
    this.state!.players = this.seats.size;
    await this.persist();
    await this.publish();
    return json({ room: this.publicState(), seatToken: seat.token, reconnectToken: seat.reconnectToken, reconnected: false });
  }

  private async socket(request: Request): Promise<Response> {
    if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") return error("WebSocket upgrade required.", 426);
    const token = bearer(request) || new URL(request.url).searchParams.get("token") || "";
    const seat = [...this.seats.values()].find(value => value.token === token);
    if (!seat) return error("Invalid seat token.", 401);
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    for (const existing of this.ctx.getWebSockets()) {
      const attachment = existing.deserializeAttachment() as SocketAttachment | null;
      if (attachment?.seatId === seat.id) existing.close(1000, "Seat reconnected");
    }
    server.serializeAttachment({ seatId: seat.id } satisfies SocketAttachment);
    this.ctx.acceptWebSocket(server);
    seat.connected = true;
    const game = this.state!.game;
    if (game?.started && game.turnIndex < 0) this.advanceTurn();
    await this.persist();
    server.send(JSON.stringify({ type: "welcome", protocol: ProtocolVersion, room: this.publicState(), seat: this.publicSeat(seat), players: this.playerList() }));
    server.send(JSON.stringify({ type: "game-state", game: this.gamePacket() }));
    this.broadcast({ type: "players", players: this.playerList() });
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(socket: WebSocket, message: string | ArrayBuffer): Promise<void> {
    if (typeof message !== "string" || message.length > 256 * 1024) return socket.close(1009, "Control message too large");
    const attachment = socket.deserializeAttachment() as SocketAttachment | null;
    const seat = attachment ? this.seats.get(attachment.seatId) : undefined;
    if (!seat) return socket.close(1008, "Unknown seat");
    let packet: { type?: string; payload?: unknown };
    try { packet = JSON.parse(message); } catch { return socket.send(JSON.stringify({ type: "error", message: "Invalid JSON message." })); }
    const type = cleanText(packet.type, 48);
    if (!type) return;
    if (type.startsWith("host:") && !seat.host)
      return socket.send(JSON.stringify({ type: "error", message: "Only the host may perform that action." }));
    if (type === "host:configure") return this.configureGame(seat, packet.payload);
    if (type === "host:start") return this.startGame(seat);
    if (type === "host:new-game") return this.startGame(seat, true);
    if (type === "player:draw") return this.drawCard(seat);
    if (type === "player:score") return this.score(seat, packet.payload);
    if (type === "host:force-pass") return this.forcePass(seat);
    if (type === "host:reset") return this.resetGame(seat);
    if (type === "player:volunteer") return this.volunteer(seat, packet.payload);
    if (type === "host:end") return this.endGame(seat);
    if (type === "player:tie-break") return this.tieBreakVote(seat, packet.payload);
    if (type === "host:remove") return this.removePlayer(seat, packet.payload);
    if (type === "host:close") return this.closeRoom(seat);
    socket.send(JSON.stringify({ type: "error", message: "Unknown relay game command." }));
  }

  async webSocketClose(socket: WebSocket): Promise<void> {
    const attachment = socket.deserializeAttachment() as SocketAttachment | null;
    const seat = attachment ? this.seats.get(attachment.seatId) : undefined;
    if (seat) {
      const wasCurrentTurn = this.currentPlayer() === seat.name;
      seat.connected = false;
      const game = this.state?.game;
      if (wasCurrentTurn && game?.started && !game.pendingVolunteer && !game.scoringDrawer) this.advanceTurn();
    }
    await this.persist();
    this.broadcast({ type: "players", players: this.playerList() });
    this.broadcastGameState();
  }

  async webSocketError(socket: WebSocket): Promise<void> { await this.webSocketClose(socket); }

  private async uploadDeck(request: Request): Promise<Response> {
    if (bearer(request) !== this.state!.hostToken) return error("Only the host may upload the room deck.", 403);
    const length = Number(request.headers.get("content-length") ?? 0);
    if (!request.body || length <= 0 || length > MaxDeckBytes) return error("Deck bundle must be between 1 byte and 100 MB.", 413);
    const hash = request.headers.get("x-levemetes-sha256")?.toLowerCase() ?? "";
    if (!/^[a-f0-9]{64}$/.test(hash)) return error("A SHA-256 deck hash is required.");
    const key = `rooms/${this.state!.code}/${randomToken(18)}.levemetesdeck`;
    if (this.state!.deckKey) await this.env.DECKS.delete(this.state!.deckKey);
    await this.env.DECKS.put(key, request.body, { httpMetadata: { contentType: "application/octet-stream" }, customMetadata: { sha256: hash } });
    this.state!.deckKey = key;
    this.state!.deckHash = hash;
    this.state!.deckBytes = length;
    await this.persist();
    this.broadcast({ type: "deck-ready", hash, bytes: length });
    return json({ hash, bytes: length });
  }

  private async downloadDeck(request: Request): Promise<Response> {
    const token = bearer(request);
    if (![...this.seats.values()].some(seat => seat.token === token) || !this.state!.deckKey) return error("Deck is unavailable.", 404);
    const object = await this.env.DECKS.get(this.state!.deckKey);
    if (!object) return error("Deck is unavailable.", 404);
    return new Response(object.body, { headers: {
      "content-type": "application/octet-stream", "content-length": String(object.size),
      "x-levemetes-sha256": this.state!.deckHash ?? "", "cache-control": "no-store",
    }});
  }

  async alarm(): Promise<void> {
    const pending = this.state?.game?.pendingVolunteer;
    if (pending && pending.deadline <= Date.now()) {
      const candidates = [...this.seats.values()].filter(seat => seat.connected && seat.name !== pending.drawer);
      if (candidates.length) await this.resolveVolunteer(candidates[crypto.getRandomValues(new Uint32Array(1))[0] % candidates.length], true);
      else {
        this.state!.game!.pendingVolunteer = undefined;
        this.advanceTurn();
        await this.persist();
        this.broadcastGameState();
      }
      await this.ctx.storage.setAlarm(this.state!.expiresAt);
      return;
    }
    if (this.state?.deckKey) await this.env.DECKS.delete(this.state.deckKey);
    if (this.state) await this.env.DIRECTORY.getByName("global").fetch("https://directory/remove", {
      method: "POST", body: JSON.stringify({ code: this.state.code }), headers: { "content-type": "application/json" },
    });
    for (const socket of this.ctx.getWebSockets()) socket.close(1001, "Room expired");
    await this.ctx.storage.deleteAll();
    this.state = undefined;
    this.seats.clear();
  }

  private async configureGame(seat: Seat, payload: unknown): Promise<void> {
    if (!seat.host || this.state!.started || !payload || typeof payload !== "object") return;
    const input = payload as { cards?: Array<{ id?: unknown; keyword?: unknown }>; scoringEnabled?: unknown };
    const cards = (input.cards ?? []).map(card => ({ id: cleanText(card.id, 64), keyword: cleanText(card.keyword, 32) }))
      .filter(card => /^[0-9a-f-]{36}$/i.test(card.id)).slice(0, 10000);
    if (!cards.length) return this.sendError(seat, "The selected intensity has no playable cards.");
    const scores: Record<string, number> = {};
    for (const player of this.seats.values()) scores[player.name] = 0;
    this.state!.game = {
      configuredCards: cards.map(card => card.id), keywords: Object.fromEntries(cards.map(card => [card.id, card.keyword])),
      drawPile: this.shuffle(cards.map(card => card.id)), turnOrder: [], turnIndex: -1, started: false,
      scoringEnabled: input.scoringEnabled === true, scores, scoringDrawer: "", eligibleVoters: [], votes: {},
      tieBreakCandidates: [], eligibleTieVoters: [], tieVotes: {},
    };
    this.state!.started = false;
    await this.persist();
    await this.publish();
    this.broadcastGameState();
  }

  private async startGame(seat: Seat, resetGame = false): Promise<void> {
    const game = this.state!.game;
    if (!seat.host || !game || !this.state!.deckKey) return this.sendError(seat, "Upload and configure the room deck before starting.");
    const connected = [...this.seats.values()].filter(player => player.connected).map(player => player.name);
    if (!connected.length) return this.sendError(seat, "There are no connected players.");
    if (resetGame) {
      game.drawPile = this.shuffle([...game.configuredCards]);
      game.scores = Object.fromEntries([...this.seats.values()].map(player => [player.name, 0]));
    }
    game.turnOrder = this.shuffle(connected);
    game.turnIndex = 0;
    game.started = true;
    game.currentCardId = undefined; game.currentDrawer = undefined; game.pendingVolunteer = undefined;
    game.scoringDrawer = ""; game.eligibleVoters = []; game.votes = {};
    game.tieBreakCandidates = []; game.eligibleTieVoters = []; game.tieVotes = {};
    this.state!.started = true;
    await this.persist(); await this.publish();
    await this.ctx.storage.setAlarm(this.state!.expiresAt);
    this.broadcast({ type: "game-started", currentPlayer: this.currentPlayer(), turnOrder: game.turnOrder,
      message: resetGame ? "The host started a new game." : "The host started the game." });
    this.broadcastGameState();
  }

  private async drawCard(seat: Seat): Promise<void> {
    const game = this.state!.game;
    if (!game?.started) return this.sendError(seat, "The host has not started the game.");
    if (game.pendingVolunteer) return this.sendError(seat, "Resolve the BLIND VOLUNTEER card first.");
    if (game.scoringDrawer) return this.sendError(seat, "Wait for all eligible players to score the current turn.");
    if (seat.name !== this.currentPlayer()) return this.sendError(seat, `It is ${this.currentPlayer()}'s turn.`);
    const cardId = game.drawPile.shift();
    if (!cardId) return this.sendError(seat, "No cards remain. The host must shuffle and reset.");
    game.currentCardId = cardId; game.currentDrawer = seat.name; game.randomTarget = undefined;
    const keyword = game.keywords[cardId]?.toLowerCase() ?? "";
    if (keyword === "blindvolunteer" || keyword === "blind volunteer") {
      const pending = { resolutionId: crypto.randomUUID(), cardId, drawer: seat.name, deadline: Date.now() + 30000 };
      game.pendingVolunteer = pending;
      this.sendToSeat(seat, { type: "card-drawn", cardId, drawer: seat.name, remaining: game.drawPile.length });
      this.broadcastExcept(seat.id, { type: "volunteer-prompt", resolutionId: pending.resolutionId, drawer: seat.name,
        deadline: pending.deadline, remaining: game.drawPile.length });
      await this.ctx.storage.setAlarm(pending.deadline);
    } else {
      if (!game.scoringEnabled) this.advanceTurn();
      this.broadcast({ type: "card-drawn", cardId, drawer: seat.name, remaining: game.drawPile.length });
      if (keyword === "random") {
        const candidates = [...this.seats.values()].filter(player => player.connected && player.id !== seat.id);
        if (candidates.length) {
          game.randomTarget = candidates[crypto.getRandomValues(new Uint32Array(1))[0] % candidates.length].name;
          this.broadcast({ type: "random-target", drawer: seat.name, selectedPlayer: game.randomTarget });
        }
      }
      if (game.scoringEnabled) this.beginScoring(seat.name);
    }
    await this.persist();
    this.broadcastGameState();
  }

  private async score(seat: Seat, payload: unknown): Promise<void> {
    const game = this.state!.game;
    const value = typeof payload === "number" ? payload : Number((payload as { value?: unknown } | null)?.value);
    if (!game?.scoringDrawer || !game.eligibleVoters.includes(seat.name) || seat.name in game.votes) return;
    if (!Number.isInteger(value) || value < 0 || value > 5) return this.sendError(seat, "Scores must be between 0 and 5.");
    game.votes[seat.name] = value;
    if (game.eligibleVoters.every(name => name in game.votes)) this.completeScoring();
    await this.persist(); this.broadcastGameState();
  }

  private async forcePass(seat: Seat): Promise<void> {
    const game = this.state!.game;
    if (!seat.host || !game?.scoringDrawer) return;
    for (const name of game.eligibleVoters) if (!(name in game.votes)) game.votes[name] = 3;
    this.completeScoring();
    await this.persist(); this.broadcast({ type: "status", message: "The host assigned 3 points to each missing vote." });
    this.broadcastGameState();
  }

  private async resetGame(seat: Seat): Promise<void> {
    const game = this.state!.game;
    if (!seat.host || !game) return;
    game.drawPile = this.shuffle([...game.configuredCards]);
    game.currentCardId = undefined; game.currentDrawer = undefined; game.randomTarget = undefined;
    game.pendingVolunteer = undefined; game.scoringDrawer = ""; game.eligibleVoters = []; game.votes = {};
    await this.persist();
    await this.ctx.storage.setAlarm(this.state!.expiresAt);
    this.broadcast({ type: "reset", remaining: game.drawPile.length }); this.broadcastGameState();
  }

  private async volunteer(seat: Seat, payload: unknown): Promise<void> {
    const game = this.state!.game;
    const resolutionId = cleanText((payload as { resolutionId?: unknown } | null)?.resolutionId, 64);
    if (!game?.pendingVolunteer || game.pendingVolunteer.resolutionId !== resolutionId || game.pendingVolunteer.drawer === seat.name) return;
    await this.resolveVolunteer(seat, false);
  }

  private async resolveVolunteer(seat: Seat, automatic: boolean): Promise<void> {
    const game = this.state!.game;
    const pending = game?.pendingVolunteer;
    if (!game || !pending) return;
    game.pendingVolunteer = undefined;
    this.broadcast({ type: "volunteer-resolved", resolutionId: pending.resolutionId, cardId: pending.cardId,
      drawer: pending.drawer, selectedPlayer: seat.name, automatic, remaining: game.drawPile.length });
    if (game.scoringEnabled) this.beginScoring(pending.drawer); else this.advanceTurn();
    await this.persist();
    await this.ctx.storage.setAlarm(this.state!.expiresAt);
    this.broadcastGameState();
  }

  private async endGame(seat: Seat): Promise<void> {
    const game = this.state!.game;
    if (!seat.host || !game) return;
    if (!game.started) return this.sendError(seat, "There is no active game to end.");
    game.started = false; game.scoringDrawer = ""; game.pendingVolunteer = undefined;
    this.state!.started = false;
    const best = Math.max(0, ...Object.values(game.scores));
    const winners = Object.entries(game.scores).filter(([, score]) => score === best).map(([name]) => name);
    await this.persist();
    await this.ctx.storage.setAlarm(this.state!.expiresAt);
    await this.publish();
    const eligible = [...this.seats.values()].filter(player => player.connected && !winners.includes(player.name)).map(player => player.name);
    if (winners.length > 1 && eligible.length > 0) {
      game.tieBreakCandidates = winners; game.eligibleTieVoters = eligible; game.tieVotes = {};
      await this.persist();
      this.broadcast({ type: "tie-break-started", candidates: winners, eligibleVoters: eligible });
    } else this.broadcast({ type: "game-ended", scores: game.scores, winners });
    this.broadcastGameState();
  }

  private async tieBreakVote(seat: Seat, payload: unknown): Promise<void> {
    const game = this.state!.game;
    const candidate = cleanText((payload as { candidate?: unknown } | null)?.candidate, 100);
    if (!game || !game.eligibleTieVoters.includes(seat.name) || seat.name in game.tieVotes || !game.tieBreakCandidates.includes(candidate)) return;
    game.tieVotes[seat.name] = candidate;
    if (game.eligibleTieVoters.every(name => name in game.tieVotes)) {
      const totals = Object.fromEntries(game.tieBreakCandidates.map(name => [name, 0])) as Record<string, number>;
      for (const voted of Object.values(game.tieVotes)) totals[voted]++;
      const best = Math.max(...Object.values(totals));
      const winners = Object.entries(totals).filter(([, count]) => count === best).map(([name]) => name);
      game.tieBreakCandidates = []; game.eligibleTieVoters = []; game.tieVotes = {};
      this.broadcast({ type: "game-ended", scores: game.scores, winners });
    }
    await this.persist(); this.broadcastGameState();
  }

  private async removePlayer(seat: Seat, payload: unknown): Promise<void> {
    if (!seat.host) return;
    const name = cleanText((payload as { name?: unknown } | null)?.name, 100);
    const removed = [...this.seats.values()].find(player => !player.host && player.name === name);
    if (!removed) return;
    this.seats.delete(removed.id);
    for (const socket of this.ctx.getWebSockets()) {
      const attachment = socket.deserializeAttachment() as SocketAttachment | null;
      if (attachment?.seatId === removed.id) socket.close(1008, "Removed by host");
    }
    const game = this.state!.game;
    if (game) {
      const oldCurrent = this.currentPlayer();
      game.turnOrder = game.turnOrder.filter(player => player !== name);
      game.eligibleVoters = game.eligibleVoters.filter(player => player !== name);
      delete game.votes[name]; delete game.scores[name];
      if (game.scoringDrawer && game.eligibleVoters.every(player => player in game.votes)) this.completeScoring();
      if (oldCurrent === name && game.started) game.turnIndex %= Math.max(1, game.turnOrder.length);
    }
    this.state!.players = this.seats.size;
    await this.persist(); await this.publish();
    this.broadcast({ type: "players", players: this.playerList() }); this.broadcastGameState();
  }

  private async closeRoom(seat: Seat): Promise<void> {
    if (!seat.host) return;
    if (this.state?.game) this.state.game.pendingVolunteer = undefined;
    this.state!.expiresAt = 0;
    this.broadcast({ type: "room-closed", message: "The host closed the relay room." });
    await this.alarm();
  }

  private beginScoring(drawer: string) {
    const game = this.state!.game!;
    game.scoringDrawer = drawer;
    game.votes = {};
    game.eligibleVoters = [...this.seats.values()].filter(seat => seat.connected && seat.name !== drawer).map(seat => seat.name);
    if (!game.eligibleVoters.length) { game.scoringDrawer = ""; this.advanceTurn(); }
  }
  private completeScoring() {
    const game = this.state!.game!;
    game.scores[game.scoringDrawer] = (game.scores[game.scoringDrawer] ?? 0) + Object.values(game.votes).reduce((a, b) => a + b, 0);
    game.scoringDrawer = ""; game.eligibleVoters = []; game.votes = {}; this.advanceTurn();
  }
  private advanceTurn() {
    const game = this.state!.game!;
    if (!game.turnOrder.length) { game.turnIndex = -1; return; }
    for (let offset = 1; offset <= game.turnOrder.length; offset++) {
      const candidateIndex = (Math.max(-1, game.turnIndex) + offset) % game.turnOrder.length;
      const candidate = [...this.seats.values()].find(seat => seat.name === game.turnOrder[candidateIndex]);
      if (candidate?.connected) { game.turnIndex = candidateIndex; return; }
    }
    game.turnIndex = -1;
  }
  private currentPlayer() {
    const game = this.state?.game;
    return game && game.turnIndex >= 0 && game.turnOrder.length ? game.turnOrder[game.turnIndex % game.turnOrder.length] : "";
  }
  private gamePacket() {
    const game = this.state?.game;
    return game ? { started: game.started, currentPlayer: this.currentPlayer(), turnOrder: game.turnOrder,
      remaining: game.drawPile.length, scoringEnabled: game.scoringEnabled, scores: game.scores,
      scoringDrawer: game.scoringDrawer, eligibleVoters: game.eligibleVoters, submittedVoters: Object.keys(game.votes),
      tieBreakCandidates: game.tieBreakCandidates, eligibleTieVoters: game.eligibleTieVoters,
      submittedTieVoters: Object.keys(game.tieVotes),
      currentCardId: game.pendingVolunteer ? undefined : game.currentCardId, currentDrawer: game.currentDrawer,
      randomTarget: game.randomTarget, pendingVolunteer: game.pendingVolunteer ? {
        resolutionId: game.pendingVolunteer.resolutionId, drawer: game.pendingVolunteer.drawer, deadline: game.pendingVolunteer.deadline } : undefined } : null;
  }
  private broadcastGameState() { this.broadcast({ type: "game-state", game: this.gamePacket() }); }
  private shuffle<T>(values: T[]) {
    for (let index = values.length - 1; index > 0; index--) {
      const swap = crypto.getRandomValues(new Uint32Array(1))[0] % (index + 1);
      [values[index], values[swap]] = [values[swap], values[index]];
    }
    return values;
  }
  private sendError(seat: Seat, message: string) { this.sendToSeat(seat, { type: "error", message }); }
  private sendToSeat(seat: Seat, value: unknown) {
    const payload = JSON.stringify(value);
    for (const socket of this.ctx.getWebSockets()) {
      const attachment = socket.deserializeAttachment() as SocketAttachment | null;
      if (attachment?.seatId === seat.id) try { socket.send(payload); } catch { }
    }
  }
  private broadcastExcept(seatId: string, value: unknown) {
    const payload = JSON.stringify(value);
    for (const socket of this.ctx.getWebSockets()) {
      const attachment = socket.deserializeAttachment() as SocketAttachment | null;
      if (attachment?.seatId !== seatId) try { socket.send(payload); } catch { }
    }
  }

  private newSeat(name: string, host: boolean): Seat {
    return { id: crypto.randomUUID(), name, token: randomToken(), reconnectToken: randomToken(32), host, connected: false };
  }
  private publicSeat(seat: Seat) { return { id: seat.id, name: seat.name, host: seat.host, connected: seat.connected }; }
  private playerList() { return [...this.seats.values()].map(seat => this.publicSeat(seat)); }
  private publicState(): RoomSummary & { deckReady: boolean; deckHash?: string; deckBytes?: number } {
    const state = this.state!;
    return { code: state.code, name: state.name, host: state.host, visibility: state.visibility,
      intensity: state.intensity, passwordProtected: state.passwordProtected, players: state.players,
      capacity: state.capacity, started: state.started, expiresAt: state.expiresAt,
      deckReady: !!state.deckKey, deckHash: state.deckHash, deckBytes: state.deckBytes };
  }
  private async persist() {
    await this.ctx.storage.put({ state: this.state!, seats: [...this.seats.values()].map(seat => ({ ...seat, connected: false })) });
  }
  private async publish() {
    await this.env.DIRECTORY.getByName("global").fetch("https://directory/upsert", {
      method: "POST", body: JSON.stringify(this.publicState()), headers: { "content-type": "application/json" },
    });
  }
  private broadcast(value: unknown, target?: "host") {
    const payload = JSON.stringify(value);
    for (const socket of this.ctx.getWebSockets()) {
      const attachment = socket.deserializeAttachment() as SocketAttachment | null;
      const seat = attachment ? this.seats.get(attachment.seatId) : undefined;
      if (!seat || (target === "host" && !seat.host)) continue;
      try { socket.send(payload); } catch { /* close event will reconcile */ }
    }
  }
}
