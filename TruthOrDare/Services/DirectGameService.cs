using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TruthOrDare.Models;

namespace TruthOrDare.Services;

public sealed class DirectGameService : IDisposable
{
    private const int MaxPlayers = 8;
    private const int MaxPlayerNameLength = 64;
    private const int MaxFrameBytes = 105 * 1024 * 1024;
    private static readonly byte[] HandshakeMagic = "LMH1"u8.ToArray();
    private readonly Action<Exception, string>? logWarning;
    private readonly ConcurrentQueue<DirectGameEvent> events = new();
    private readonly SemaphoreSlim connectionSlots = new(MaxPlayers - 1, MaxPlayers - 1);
    private readonly object stateLock = new();
    private readonly List<Peer> peers = [];
    private readonly Queue<Guid> drawPile = new();
    private CancellationTokenSource? cancellation;
    private TcpListener? listener;
    private Peer? hostPeer;
    private byte[]? sessionSecret;
    private Guid sessionId;
    private byte[]? lockedBundle;
    private string playerName = string.Empty;
    private string advertisedHost = string.Empty;
    private int port;
    private int remaining;
    private CardCategory category;
    private DirectGameMode mode;
    private IReadOnlyList<string> playerNames = [];
    private readonly List<string> turnOrder = [];
    private bool gameStarted;
    private int currentTurnIndex = -1;

    public DirectGameService(Action<Exception, string>? logWarning = null) => this.logWarning = logWarning;

    public DirectGameMode Mode { get { lock (stateLock) return mode; } }
    public bool IsConnected => Mode is DirectGameMode.Hosting or DirectGameMode.Joined;
    public bool IsHost => Mode == DirectGameMode.Hosting;
    public int Remaining { get { lock (stateLock) return remaining; } }
    public CardCategory Category { get { lock (stateLock) return category; } }
    public string InviteText { get; private set; } = string.Empty;
    public IReadOnlyList<string> Players { get { lock (stateLock) return playerNames; } }
    public IReadOnlyList<string> TurnOrder { get { lock (stateLock) return turnOrder.ToArray(); } }
    public bool GameStarted { get { lock (stateLock) return gameStarted; } }
    public string CurrentPlayer { get { lock (stateLock) return CurrentPlayerLocked(); } }

    public static async Task<string> DiscoverPublicAddressAsync(CancellationToken token = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var value = (await client.GetStringAsync("https://api.ipify.org", token)).Trim();
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("The public-address service did not return a valid IPv4 address.");
        return value;
    }

    public void StartHosting(string name, string publicAddress, int listenPort, Deck deck, CardCategory selectedCategory, byte[] deckBundle)
    {
        Stop();
        name = ValidateName(name);
        publicAddress = publicAddress.Trim();
        if (string.IsNullOrWhiteSpace(publicAddress) || publicAddress.Length > 255 || publicAddress.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Enter a valid public IP address or DNS name other players will use to reach the host.");
        if (listenPort is < 1024 or > 65535) throw new InvalidOperationException("Choose a port between 1024 and 65535.");
        if (!deck.Cards.Any(card => card.Category.HasFlag(selectedCategory))) throw new InvalidOperationException("The selected deck has no cards in this intensity category.");
        if (deckBundle.Length > MaxFrameBytes - 1024) throw new InvalidOperationException("This deck is too large for Direct Private Game.");

        cancellation = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Any, listenPort);
        listener.Start(MaxPlayers - 1);
        sessionSecret = RandomNumberGenerator.GetBytes(32);
        sessionId = Guid.NewGuid();
        lockedBundle = deckBundle;
        playerName = name;
        advertisedHost = publicAddress;
        port = listenPort;
        category = selectedCategory;
        ResetPile(deck);
        mode = DirectGameMode.Hosting;
        playerNames = [name + " (Host)"];
        InviteText = EncodeInvite(new Invite(publicAddress, listenPort, sessionId, Base64Url(sessionSecret)));
        events.Enqueue(new DirectGameEvent(DirectGameEventType.Status, "Direct room created. Share the invitation only with trusted players."));
        _ = AcceptLoopAsync(cancellation.Token);
    }

    public void Join(string name, string invitation)
    {
        Stop();
        name = ValidateName(name);
        var invite = DecodeInvite(invitation);
        cancellation = new CancellationTokenSource();
        sessionId = invite.SessionId;
        sessionSecret = FromBase64Url(invite.Secret);
        playerName = name;
        advertisedHost = invite.Host;
        port = invite.Port;
        mode = DirectGameMode.Connecting;
        events.Enqueue(new DirectGameEvent(DirectGameEventType.Status, "Connecting to the direct host…"));
        _ = ConnectAsync(invite, name, cancellation.Token);
    }

    public void RequestDraw()
    {
        if (IsHost) HostDraw(playerName);
        else if (Mode == DirectGameMode.Joined && hostPeer is not null)
            _ = hostPeer.SendJsonAsync(PacketType.DrawRequest, new DrawRequest(playerName), cancellation?.Token ?? CancellationToken.None);
    }

    public void StartGame()
    {
        if (!IsHost) throw new InvalidOperationException("Only the host can start the game.");
        GameStateNotice state;
        lock (stateLock)
        {
            var names = new List<string> { playerName };
            names.AddRange(peers.Select(peer => peer.Name));
            if (names.Count == 0) throw new InvalidOperationException("There are no players in the room.");
            for (var index = names.Count - 1; index > 0; index--)
            {
                var swap = RandomNumberGenerator.GetInt32(index + 1);
                (names[index], names[swap]) = (names[swap], names[index]);
            }
            turnOrder.Clear();
            turnOrder.AddRange(names);
            currentTurnIndex = 0;
            gameStarted = true;
            state = MakeGameStateLocked();
        }
        BroadcastJson(PacketType.GameState, state);
        events.Enqueue(new DirectGameEvent(DirectGameEventType.GameStarted, $"Game started. {state.CurrentPlayer} draws first."));
    }

    public void RemovePlayer(string name)
    {
        if (!IsHost) throw new InvalidOperationException("Only the host can remove players.");
        Peer? removed;
        GameStateNotice state;
        lock (stateLock)
        {
            removed = peers.FirstOrDefault(peer => peer.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed is not null) peers.Remove(removed);
            var removedIndex = turnOrder.FindIndex(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removedIndex >= 0)
            {
                turnOrder.RemoveAt(removedIndex);
                if (removedIndex < currentTurnIndex) currentTurnIndex--;
                if (currentTurnIndex >= turnOrder.Count) currentTurnIndex = 0;
            }
            UpdatePlayerNamesLocked();
            state = MakeGameStateLocked();
        }
        removed?.Dispose();
        BroadcastPlayerList();
        BroadcastJson(PacketType.GameState, state);
        events.Enqueue(new DirectGameEvent(DirectGameEventType.Status, $"{name} was removed from the game."));
    }

    public void ResetSharedPile(Deck deck)
    {
        if (!IsHost) throw new InvalidOperationException("Only the host can shuffle and reset the shared draw pile.");
        ResetPile(deck);
        var packet = new ResetNotice(Remaining);
        events.Enqueue(new DirectGameEvent(DirectGameEventType.Reset, "The host shuffled and reset the shared deck.", Remaining: Remaining));
        BroadcastJson(PacketType.Reset, packet);
    }

    public bool TryDequeue(out DirectGameEvent gameEvent) => events.TryDequeue(out gameEvent!);

    public void Stop()
    {
        var oldCancellation = cancellation;
        cancellation = null;
        try { oldCancellation?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        listener = null;
        lock (stateLock)
        {
            foreach (var peer in peers.ToList()) peer.Dispose();
            peers.Clear();
            hostPeer?.Dispose();
            hostPeer = null;
            mode = DirectGameMode.Disconnected;
            remaining = 0;
            playerNames = [];
            drawPile.Clear();
            turnOrder.Clear();
            gameStarted = false;
            currentTurnIndex = -1;
        }
        if (sessionSecret is not null) CryptographicOperations.ZeroMemory(sessionSecret);
        sessionSecret = null;
        lockedBundle = null;
        InviteText = string.Empty;
        oldCancellation?.Dispose();
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener is not null)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token);
                if (!connectionSlots.Wait(0)) { client.Dispose(); continue; }
                _ = HandleGuestAsync(client, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { ReportFailure("Could not accept a direct connection.", ex); }
        }
    }

    private async Task HandleGuestAsync(TcpClient client, CancellationToken token)
    {
        Peer? peer = null;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshakeCancellation.CancelAfter(TimeSpan.FromSeconds(15));
            var handshakeToken = handshakeCancellation.Token;
            var header = await ReadExactAsync(stream, 36, handshakeToken);
            if (!header.AsSpan(0, 4).SequenceEqual(HandshakeMagic) || new Guid(header.AsSpan(4, 16)) != sessionId)
                throw new InvalidDataException("Invalid Direct Private Game invitation.");
            var clientNonce = header.AsSpan(20, 16).ToArray();
            var hostNonce = RandomNumberGenerator.GetBytes(16);
            await stream.WriteAsync(hostNonce, handshakeToken);
            var keys = DeriveKeys(sessionSecret!, sessionId, clientNonce, hostNonce);
            peer = new Peer(client, new SecureChannel(stream, keys.HostToClient, keys.ClientToHost));
            var joinPacket = await peer.ReceiveAsync(handshakeToken);
            if (joinPacket.Type != PacketType.Join) throw new InvalidDataException("Expected a join request.");
            var join = Deserialize<JoinRequest>(joinPacket.Payload);
            peer.Name = ValidateName(join.PlayerName);
            lock (stateLock)
            {
                if (peers.Count + 1 >= MaxPlayers) throw new InvalidOperationException("This direct room is full.");
                if (peers.Any(item => item.Name.Equals(peer.Name, StringComparison.OrdinalIgnoreCase)) || playerName.Equals(peer.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("That player name is already in use in this room.");
                peers.Add(peer);
                UpdatePlayerNamesLocked();
            }
            await peer.SendJsonAsync(PacketType.Welcome, new Welcome(category, remaining), token);
            await peer.SendAsync(PacketType.DeckBundle, lockedBundle!, token);
            GameStateNotice currentState;
            lock (stateLock) currentState = MakeGameStateLocked();
            await peer.SendJsonAsync(PacketType.GameState, currentState, token);
            BroadcastPlayerList();
            events.Enqueue(new DirectGameEvent(DirectGameEventType.Status, $"{peer.Name} joined the room."));
            await ReceiveGuestLoopAsync(peer, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { if (peer is not null) await peer.SendJsonAsync(PacketType.Error, new ErrorNotice(ex.Message), CancellationToken.None); } catch { }
            logWarning?.Invoke(ex, "Direct guest connection ended");
        }
        finally
        {
            if (peer is not null)
            {
                lock (stateLock)
                {
                    peers.Remove(peer);
                    UpdatePlayerNamesLocked();
                    if (gameStarted && CurrentPlayerLocked().Equals(peer.Name, StringComparison.OrdinalIgnoreCase)) AdvanceTurnLocked();
                }
                peer.Dispose();
                BroadcastPlayerList();
                BroadcastGameState();
                if (!string.IsNullOrWhiteSpace(peer.Name)) events.Enqueue(new DirectGameEvent(DirectGameEventType.Status, $"{peer.Name} left the room."));
            }
            else client.Dispose();
            connectionSlots.Release();
        }
    }

    private async Task ReceiveGuestLoopAsync(Peer peer, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var packet = await peer.ReceiveAsync(token);
            if (packet.Type == PacketType.DrawRequest) HostDraw(peer.Name);
        }
    }

    private async Task ConnectAsync(Invite invite, string name, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(invite.Host, invite.Port, token);
            var stream = client.GetStream();
            var clientNonce = RandomNumberGenerator.GetBytes(16);
            var header = new byte[36];
            HandshakeMagic.CopyTo(header, 0);
            sessionId.TryWriteBytes(header.AsSpan(4, 16));
            clientNonce.CopyTo(header, 20);
            await stream.WriteAsync(header, token);
            var hostNonce = await ReadExactAsync(stream, 16, token);
            var keys = DeriveKeys(sessionSecret!, sessionId, clientNonce, hostNonce);
            var peer = new Peer(client, new SecureChannel(stream, keys.ClientToHost, keys.HostToClient)) { Name = "Host" };
            hostPeer = peer;
            await peer.SendJsonAsync(PacketType.Join, new JoinRequest(name), token);
            var welcomePacket = await peer.ReceiveAsync(token);
            if (welcomePacket.Type == PacketType.Error) throw new InvalidOperationException(Deserialize<ErrorNotice>(welcomePacket.Payload).Message);
            if (welcomePacket.Type != PacketType.Welcome) throw new InvalidDataException("The host returned an invalid welcome message.");
            var welcome = Deserialize<Welcome>(welcomePacket.Payload);
            lock (stateLock) { category = welcome.Category; remaining = welcome.Remaining; mode = DirectGameMode.Joined; }
            var deckPacket = await peer.ReceiveAsync(token);
            if (deckPacket.Type != PacketType.DeckBundle) throw new InvalidDataException("The host did not send a deck.");
            events.Enqueue(new DirectGameEvent(DirectGameEventType.DeckReceived, "The host deck was received.", deckPacket.Payload, Remaining: welcome.Remaining, Category: welcome.Category));
            var statePacket = await peer.ReceiveAsync(token);
            if (statePacket.Type != PacketType.GameState) throw new InvalidDataException("The host did not send the game state.");
            ApplyGameState(Deserialize<GameStateNotice>(statePacket.Payload));
            await ReceiveHostLoopAsync(peer, token);
        }
        catch (OperationCanceledException) { client.Dispose(); }
        catch (Exception ex)
        {
            client.Dispose();
            lock (stateLock) mode = DirectGameMode.Disconnected;
            ReportFailure("Direct connection failed", ex);
        }
    }

    private async Task ReceiveHostLoopAsync(Peer peer, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var packet = await peer.ReceiveAsync(token);
                switch (packet.Type)
                {
                    case PacketType.DrawResult:
                        var draw = Deserialize<DrawResult>(packet.Payload);
                        lock (stateLock) remaining = draw.Remaining;
                        events.Enqueue(new DirectGameEvent(DirectGameEventType.CardDrawn, $"{draw.Drawer} drew a card.", CardId: draw.CardId, Drawer: draw.Drawer, Remaining: draw.Remaining));
                        break;
                    case PacketType.Reset:
                        var reset = Deserialize<ResetNotice>(packet.Payload);
                        lock (stateLock) remaining = reset.Remaining;
                        events.Enqueue(new DirectGameEvent(DirectGameEventType.Reset, "The host shuffled and reset the shared deck.", Remaining: reset.Remaining));
                        break;
                    case PacketType.PlayerList:
                        var list = Deserialize<PlayerListNotice>(packet.Payload).Names;
                        lock (stateLock) playerNames = list;
                        events.Enqueue(new DirectGameEvent(DirectGameEventType.PlayerListChanged, "Player list updated."));
                        break;
                    case PacketType.Error:
                        events.Enqueue(new DirectGameEvent(DirectGameEventType.Error, Deserialize<ErrorNotice>(packet.Payload).Message));
                        break;
                    case PacketType.GameState:
                        ApplyGameState(Deserialize<GameStateNotice>(packet.Payload));
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (stateLock) mode = DirectGameMode.Disconnected;
            ReportFailure("The direct host connection ended", ex);
        }
    }

    private void HostDraw(string drawer)
    {
        Guid cardId;
        int count;
        lock (stateLock)
        {
            if (!gameStarted) { events.Enqueue(new DirectGameEvent(DirectGameEventType.Error, "The host has not started the game yet.")); return; }
            var current = CurrentPlayerLocked();
            if (!drawer.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                SendDrawError(drawer, $"It is {current}'s turn.");
                return;
            }
            if (drawPile.Count == 0) { events.Enqueue(new DirectGameEvent(DirectGameEventType.Error, "No shared cards remain. The host must shuffle and reset.")); return; }
            cardId = drawPile.Dequeue();
            remaining = drawPile.Count;
            count = remaining;
            AdvanceTurnLocked();
        }
        var result = new DrawResult(Guid.NewGuid(), cardId, drawer, count);
        events.Enqueue(new DirectGameEvent(DirectGameEventType.CardDrawn, $"{drawer} drew a card.", CardId: cardId, Drawer: drawer, Remaining: count));
        BroadcastJson(PacketType.DrawResult, result);
        BroadcastGameState();
    }

    private void SendDrawError(string drawer, string message)
    {
        if (drawer.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            events.Enqueue(new DirectGameEvent(DirectGameEventType.Error, message));
        else
        {
            Peer? peer;
            lock (stateLock) peer = peers.FirstOrDefault(item => item.Name.Equals(drawer, StringComparison.OrdinalIgnoreCase));
            if (peer is not null) _ = peer.SendJsonAsync(PacketType.Error, new ErrorNotice(message), cancellation?.Token ?? CancellationToken.None);
        }
    }

    private void ApplyGameState(GameStateNotice state)
    {
        lock (stateLock)
        {
            gameStarted = state.Started;
            turnOrder.Clear();
            turnOrder.AddRange(state.TurnOrder);
            currentTurnIndex = state.CurrentTurnIndex;
        }
        events.Enqueue(new DirectGameEvent(state.Started ? DirectGameEventType.GameStarted : DirectGameEventType.GameStateChanged,
            state.Started ? $"Current turn: {state.CurrentPlayer}" : "Waiting for the host to start the game."));
    }

    private void BroadcastGameState()
    {
        GameStateNotice state;
        lock (stateLock) state = MakeGameStateLocked();
        BroadcastJson(PacketType.GameState, state);
        events.Enqueue(new DirectGameEvent(DirectGameEventType.GameStateChanged,
            state.Started ? $"Current turn: {state.CurrentPlayer}" : "Waiting for the host to start the game."));
    }

    private GameStateNotice MakeGameStateLocked() => new(gameStarted, turnOrder.ToArray(), currentTurnIndex, CurrentPlayerLocked());

    private string CurrentPlayerLocked() => gameStarted && currentTurnIndex >= 0 && currentTurnIndex < turnOrder.Count
        ? turnOrder[currentTurnIndex]
        : string.Empty;

    private void AdvanceTurnLocked()
    {
        if (turnOrder.Count == 0) { currentTurnIndex = -1; return; }
        var connected = new HashSet<string>(peers.Select(peer => peer.Name), StringComparer.OrdinalIgnoreCase) { playerName };
        for (var offset = 1; offset <= turnOrder.Count; offset++)
        {
            var candidate = (currentTurnIndex + offset) % turnOrder.Count;
            if (connected.Contains(turnOrder[candidate])) { currentTurnIndex = candidate; return; }
        }
    }

    private void ResetPile(Deck deck)
    {
        var ids = deck.Cards.Where(card => card.Category.HasFlag(category)).Select(card => card.Id).ToList();
        for (var index = ids.Count - 1; index > 0; index--)
        {
            var swap = RandomNumberGenerator.GetInt32(index + 1);
            (ids[index], ids[swap]) = (ids[swap], ids[index]);
        }
        lock (stateLock)
        {
            drawPile.Clear();
            foreach (var id in ids) drawPile.Enqueue(id);
            remaining = drawPile.Count;
        }
    }

    private void BroadcastJson<T>(PacketType type, T value)
    {
        List<Peer> targets;
        lock (stateLock) targets = peers.ToList();
        foreach (var peer in targets) _ = peer.SendJsonAsync(type, value, cancellation?.Token ?? CancellationToken.None);
    }

    private void BroadcastPlayerList()
    {
        IReadOnlyList<string> names;
        lock (stateLock) names = playerNames;
        BroadcastJson(PacketType.PlayerList, new PlayerListNotice(names));
    }

    private void UpdatePlayerNamesLocked() => playerNames = [playerName + " (Host)", .. peers.Select(peer => peer.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    private void ReportFailure(string message, Exception ex) { logWarning?.Invoke(ex, message); events.Enqueue(new DirectGameEvent(DirectGameEventType.Error, $"{message}: {ex.Message}")); }
    private static string ValidateName(string name)
    {
        name = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length is 0 or > MaxPlayerNameLength || name.Any(char.IsControl))
            throw new InvalidOperationException($"Player names must be 1-{MaxPlayerNameLength} visible characters.");
        return name;
    }

    private static string EncodeInvite(Invite invite) => "LM1." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(invite));
    private static Invite DecodeInvite(string text)
    {
        text = text.Trim();
        if (text.Length > 2048 || !text.StartsWith("LM1.", StringComparison.Ordinal)) throw new InvalidDataException("This is not a valid Levemetes Direct Private Game invitation.");
        var invite = JsonSerializer.Deserialize<Invite>(FromBase64Url(text[4..])) ?? throw new InvalidDataException("The invitation is invalid.");
        if (invite.Port is < 1024 or > 65535 || invite.SessionId == Guid.Empty || FromBase64Url(invite.Secret).Length != 32)
            throw new InvalidDataException("The invitation contains invalid connection information.");
        return invite;
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string text)
    {
        text = text.Replace('-', '+').Replace('_', '/');
        text += new string('=', (4 - text.Length % 4) % 4);
        return Convert.FromBase64String(text);
    }
    private static SessionKeys DeriveKeys(byte[] secret, Guid id, byte[] clientNonce, byte[] hostNonce)
    {
        var salt = new byte[48];
        id.TryWriteBytes(salt.AsSpan(0, 16));
        clientNonce.CopyTo(salt, 16);
        hostNonce.CopyTo(salt, 32);
        var prk = HMACSHA256.HashData(salt, secret);
        return new SessionKeys(HMACSHA256.HashData(prk, "host-to-client"u8), HMACSHA256.HashData(prk, "client-to-host"u8));
    }
    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken token)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(result.AsMemory(offset, length - offset), token);
            if (read == 0) throw new EndOfStreamException("The direct connection closed unexpectedly.");
            offset += read;
        }
        return result;
    }
    private static T Deserialize<T>(byte[] payload) => JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("A direct-game message was invalid.");

    private enum PacketType : byte { Join = 1, Welcome, DeckBundle, DrawRequest, DrawResult, PlayerList, Reset, Error, GameState }
    private sealed record Invite(string Host, int Port, Guid SessionId, string Secret);
    private sealed record JoinRequest(string PlayerName);
    private sealed record Welcome(CardCategory Category, int Remaining);
    private sealed record DrawRequest(string PlayerName);
    private sealed record DrawResult(Guid DrawId, Guid CardId, string Drawer, int Remaining);
    private sealed record PlayerListNotice(IReadOnlyList<string> Names);
    private sealed record ResetNotice(int Remaining);
    private sealed record ErrorNotice(string Message);
    private sealed record GameStateNotice(bool Started, IReadOnlyList<string> TurnOrder, int CurrentTurnIndex, string CurrentPlayer);
    private sealed record SessionKeys(byte[] HostToClient, byte[] ClientToHost);

    private sealed class Peer(TcpClient client, SecureChannel channel) : IDisposable
    {
        public string Name { get; set; } = string.Empty;
        public Task SendAsync(PacketType type, byte[] payload, CancellationToken token) => channel.SendAsync(type, payload, token);
        public Task SendJsonAsync<T>(PacketType type, T value, CancellationToken token) => channel.SendAsync(type, JsonSerializer.SerializeToUtf8Bytes(value), token);
        public Task<Packet> ReceiveAsync(CancellationToken token) => channel.ReceiveAsync(token);
        public void Dispose() { channel.Dispose(); client.Dispose(); }
    }

    private sealed class SecureChannel(Stream stream, byte[] sendKey, byte[] receiveKey) : IDisposable
    {
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private ulong sendCounter;
        private ulong receiveCounter;

        public async Task SendAsync(PacketType type, byte[] payload, CancellationToken token)
        {
            if (payload.Length + 1 > MaxFrameBytes) throw new InvalidDataException("A direct-game message is too large.");
            await sendLock.WaitAsync(token);
            try
            {
                var plain = new byte[payload.Length + 1];
                plain[0] = (byte)type;
                payload.CopyTo(plain, 1);
                var cipher = new byte[plain.Length];
                var tag = new byte[16];
                var nonce = MakeNonce(sendKey, sendCounter++);
                using (var aes = new AesGcm(sendKey, 16)) aes.Encrypt(nonce, plain, cipher, tag);
                var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, cipher.Length + tag.Length);
                await stream.WriteAsync(header, token);
                await stream.WriteAsync(cipher, token);
                await stream.WriteAsync(tag, token);
            }
            finally { sendLock.Release(); }
        }

        public async Task<Packet> ReceiveAsync(CancellationToken token)
        {
            var header = await ReadExactAsync(stream, 4, token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 17 or > MaxFrameBytes + 16) throw new InvalidDataException("A direct-game frame has an invalid size.");
            var encrypted = await ReadExactAsync(stream, length, token);
            var cipherLength = length - 16;
            var plain = new byte[cipherLength];
            var nonce = MakeNonce(receiveKey, receiveCounter++);
            using (var aes = new AesGcm(receiveKey, 16)) aes.Decrypt(nonce, encrypted.AsSpan(0, cipherLength), encrypted.AsSpan(cipherLength, 16), plain);
            if (plain.Length == 0 || !Enum.IsDefined((PacketType)plain[0])) throw new InvalidDataException("A direct-game packet type is invalid.");
            return new Packet((PacketType)plain[0], plain[1..]);
        }

        private static byte[] MakeNonce(byte[] key, ulong counter)
        {
            var nonce = new byte[12];
            key.AsSpan(0, 4).CopyTo(nonce);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
            return nonce;
        }
        public void Dispose() { sendLock.Dispose(); stream.Dispose(); CryptographicOperations.ZeroMemory(sendKey); CryptographicOperations.ZeroMemory(receiveKey); }
    }
    private sealed record Packet(PacketType Type, byte[] Payload);
}

public enum DirectGameMode { Disconnected, Connecting, Hosting, Joined }
public enum DirectGameEventType { Status, Error, DeckReceived, CardDrawn, Reset, PlayerListChanged, GameStarted, GameStateChanged }
public sealed record DirectGameEvent(DirectGameEventType Type, string Message, byte[]? Bundle = null, Guid? CardId = null,
    string? Drawer = null, int Remaining = 0, CardCategory Category = CardCategory.None);
