using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TruthOrDare.Services;

public sealed class RelayGameService : IDisposable
{
    public const string DefaultEndpoint = "https://levemetes-relay.kaerlath.workers.dev";
    private const int MaxDeckBytes = 100 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly ConcurrentQueue<RelayGameEvent> events = new();
    private readonly object stateLock = new();
    private ClientWebSocket? socket;
    private CancellationTokenSource? cancellation;
    private IReadOnlyList<RelayRoomSummary> publicRooms = [];
    private IReadOnlyList<RelayPlayer> players = [];
    private RelayRoomSummary? room;
    private RelayGameState? game;
    private string seatToken = string.Empty;
    private string hostToken = string.Empty;
    private string reconnectToken = string.Empty;
    private string endpoint = DefaultEndpoint;
    private bool busy;

    public bool IsConnected { get { lock (stateLock) return socket?.State == WebSocketState.Open; } }
    public bool IsHost { get { lock (stateLock) return !string.IsNullOrWhiteSpace(hostToken); } }
    public bool IsBusy { get { lock (stateLock) return busy; } }
    public RelayRoomSummary? Room { get { lock (stateLock) return room; } }
    public IReadOnlyList<RelayRoomSummary> PublicRooms { get { lock (stateLock) return publicRooms; } }
    public IReadOnlyList<RelayPlayer> Players { get { lock (stateLock) return players; } }
    public RelayGameState? Game { get { lock (stateLock) return game; } }
    public string ReconnectToken { get { lock (stateLock) return reconnectToken; } }

    public void SetEndpoint(string value)
    {
        value = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The relay address must be a valid HTTPS address.");
        lock (stateLock) endpoint = value;
    }

    public async Task CheckHealthAsync(CancellationToken token = default)
    {
        using var response = await http.GetAsync($"{Endpoint()}/health", token);
        response.EnsureSuccessStatusCode();
        events.Enqueue(new RelayGameEvent(RelayGameEventType.Status, "The Levemetes relay is online."));
    }

    public async Task RefreshRoomsAsync(CancellationToken token = default)
    {
        SetBusy(true);
        try
        {
            using var response = await http.GetAsync($"{Endpoint()}/api/v1/rooms", token);
            await EnsureSuccess(response);
            var rooms = await JsonSerializer.DeserializeAsync<List<RelayRoomSummary>>(await response.Content.ReadAsStreamAsync(token), JsonOptions, token) ?? [];
            lock (stateLock) publicRooms = rooms;
            events.Enqueue(new RelayGameEvent(RelayGameEventType.RoomListChanged, $"Found {rooms.Count} public relay room{(rooms.Count == 1 ? string.Empty : "s")}."));
        }
        catch (Exception ex) { QueueError("Could not load public relay rooms.", ex); }
        finally { SetBusy(false); }
    }

    public async Task CreateRoomAsync(string roomName, string character, bool isPrivate, string password,
        IReadOnlyList<string> intensity, byte[] deckBundle, IReadOnlyList<RelayCardDefinition> cards,
        bool scoringEnabled, CancellationToken token = default)
    {
        SetBusy(true);
        try
        {
            var input = new { name = roomName, host = character, visibility = isPrivate ? "private" : "public", password, intensity };
            using var create = new StringContent(JsonSerializer.Serialize(input, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"{Endpoint()}/api/v1/rooms", create, token);
            await EnsureSuccess(response);
            var joined = await ReadJoin(response, token);
            lock (stateLock) { room = joined.Room; seatToken = joined.SeatToken; hostToken = joined.HostToken ?? string.Empty; reconnectToken = joined.ReconnectToken; }
            await UploadDeckAsync(deckBundle, token);
            await ConnectSocketAsync(token);
            await SendAsync("host:configure", new { cards, scoringEnabled }, token);
            events.Enqueue(new RelayGameEvent(RelayGameEventType.RoomJoined, $"Created relay room {joined.Room.Code}.", Room: joined.Room));
        }
        catch (Exception ex) { QueueError("Could not create the relay room.", ex); await LeaveAsync(); }
        finally { SetBusy(false); }
    }

    public async Task JoinRoomAsync(string code, string character, string password, string savedReconnectToken = "", CancellationToken token = default)
    {
        SetBusy(true);
        try
        {
            code = code.Trim().ToUpperInvariant();
            var joinUrl = $"{Endpoint()}/api/v1/rooms/{Uri.EscapeDataString(code)}/join";
            var reconnecting = !string.IsNullOrWhiteSpace(savedReconnectToken);
            var input = reconnecting
                ? new Dictionary<string, string> { ["reconnectToken"] = savedReconnectToken }
                : new Dictionary<string, string> { ["name"] = character, ["password"] = password };
            using var content = new StringContent(JsonSerializer.Serialize(input, JsonOptions), Encoding.UTF8, "application/json");
            var response = await http.PostAsync(joinUrl, content, token);
            if (reconnecting && !response.IsSuccessStatusCode)
            {
                response.Dispose();
                input = new Dictionary<string, string> { ["name"] = character, ["password"] = password };
                using var freshContent = new StringContent(JsonSerializer.Serialize(input, JsonOptions), Encoding.UTF8, "application/json");
                response = await http.PostAsync(joinUrl, freshContent, token);
            }
            using (response)
            {
            await EnsureSuccess(response);
            var joined = await ReadJoin(response, token);
            lock (stateLock) { room = joined.Room; seatToken = joined.SeatToken; reconnectToken = joined.ReconnectToken; hostToken = joined.HostToken ?? string.Empty; }
            var bundle = await DownloadDeckAsync(token);
            await ConnectSocketAsync(token);
            events.Enqueue(new RelayGameEvent(RelayGameEventType.DeckReceived,
                $"Joined relay room {joined.Room.Code} and verified its synchronized deck.", Bundle: bundle, Room: joined.Room));
            }
        }
        catch (Exception ex) { QueueError("Could not join the relay room.", ex); await LeaveAsync(); }
        finally { SetBusy(false); }
    }

    public async Task SendAsync(string type, object? payload = null, CancellationToken token = default)
    {
        ClientWebSocket? current;
        lock (stateLock) current = socket;
        if (current?.State != WebSocketState.Open) throw new InvalidOperationException("Not connected to a relay room.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type, payload }, JsonOptions);
        await current.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    }

    public Task StartGameAsync() => SendAsync("host:start");
    public Task StartNewGameAsync() => SendAsync("host:new-game");
    public Task RequestDrawAsync() => SendAsync("player:draw");
    public Task SubmitScoreAsync(int value) => SendAsync("player:score", new { value });
    public Task ForcePassAsync() => SendAsync("host:force-pass");
    public Task ResetAsync() => SendAsync("host:reset");
    public Task VolunteerAsync(Guid resolutionId) => SendAsync("player:volunteer", new { resolutionId });
    public Task EndGameAsync() => SendAsync("host:end");
    public Task SubmitTieBreakAsync(string candidate) => SendAsync("player:tie-break", new { candidate });
    public Task RemovePlayerAsync(string name) => SendAsync("host:remove", new { name });
    public Task CloseRoomAsync() => SendAsync("host:close");

    public async Task LeaveAsync()
    {
        ClientWebSocket? oldSocket;
        CancellationTokenSource? oldCancellation;
        lock (stateLock)
        {
            oldSocket = socket; socket = null; oldCancellation = cancellation; cancellation = null;
            room = null; game = null; players = []; seatToken = string.Empty; hostToken = string.Empty;
        }
        try { oldCancellation?.Cancel(); } catch { }
        if (oldSocket is not null)
        {
            try { if (oldSocket.State == WebSocketState.Open) await oldSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Leaving room", CancellationToken.None); } catch { }
            oldSocket.Dispose();
        }
        oldCancellation?.Dispose();
    }

    public bool TryDequeue(out RelayGameEvent gameEvent) => events.TryDequeue(out gameEvent!);

    private async Task UploadDeckAsync(byte[] bundle, CancellationToken token)
    {
        if (bundle.Length is 0 or > MaxDeckBytes) throw new InvalidDataException("Relay deck bundles must be between 1 byte and 100 MB.");
        RelayRoomSummary current; string authorization;
        lock (stateLock) { current = room ?? throw new InvalidOperationException("No relay room is active."); authorization = hostToken; }
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Endpoint()}/api/v1/rooms/{current.Code}/deck");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization);
        request.Headers.Add("x-levemetes-sha256", Convert.ToHexString(SHA256.HashData(bundle)).ToLowerInvariant());
        request.Content = new ByteArrayContent(bundle);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await http.SendAsync(request, token);
        await EnsureSuccess(response);
    }

    private async Task<byte[]> DownloadDeckAsync(CancellationToken token)
    {
        RelayRoomSummary current; string authorization;
        lock (stateLock) { current = room ?? throw new InvalidOperationException("No relay room is active."); authorization = seatToken; }
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint()}/api/v1/rooms/{current.Code}/deck");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await EnsureSuccess(response);
        if (response.Content.Headers.ContentLength is > MaxDeckBytes) throw new InvalidDataException("The relay deck exceeds 100 MB.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, token);
        if (memory.Length > MaxDeckBytes) throw new InvalidDataException("The relay deck exceeds 100 MB.");
        var bundle = memory.ToArray();
        var expected = response.Headers.TryGetValues("x-levemetes-sha256", out var values) ? values.FirstOrDefault() : current.DeckHash;
        var actual = Convert.ToHexString(SHA256.HashData(bundle)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(expected) || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded deck failed its SHA-256 integrity check.");
        return bundle;
    }

    private async Task ConnectSocketAsync(CancellationToken token)
    {
        RelayRoomSummary current; string authorization;
        lock (stateLock) { current = room ?? throw new InvalidOperationException("No relay room is active."); authorization = seatToken; }
        var uri = new Uri($"{Endpoint().Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)}/api/v1/rooms/{current.Code}/socket");
        var newSocket = new ClientWebSocket();
        newSocket.Options.SetRequestHeader("Authorization", $"Bearer {authorization}");
        var newCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await newSocket.ConnectAsync(uri, token);
        lock (stateLock) { socket = newSocket; cancellation = newCancellation; }
        _ = ReceiveLoopAsync(newSocket, newCancellation.Token);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket activeSocket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!token.IsCancellationRequested && activeSocket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await activeSocket.ReceiveAsync(buffer, token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 256 * 1024) throw new InvalidDataException("Relay control message exceeded 256 KB.");
                } while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(message.ToArray());
                HandleSocketMessage(document.RootElement);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { QueueError("The relay connection was interrupted.", ex); }
    }

    private void HandleSocketMessage(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() ?? string.Empty : string.Empty;
        if ((type == "welcome" || type == "players") && root.TryGetProperty("players", out var playerValue))
        {
            var updated = playerValue.Deserialize<List<RelayPlayer>>(JsonOptions) ?? [];
            lock (stateLock) players = updated;
            events.Enqueue(new RelayGameEvent(RelayGameEventType.PlayersChanged, "Relay player list updated."));
            return;
        }
        if (type == "game-state" && root.TryGetProperty("game", out var gameValue))
        {
            var updated = gameValue.ValueKind == JsonValueKind.Null ? null : gameValue.Deserialize<RelayGameState>(JsonOptions);
            lock (stateLock) game = updated;
            events.Enqueue(new RelayGameEvent(RelayGameEventType.GameStateChanged, "Relay game state updated."));
        }
        else if (type == "card-drawn")
        {
            events.Enqueue(new RelayGameEvent(RelayGameEventType.CardDrawn, "A relay card was drawn.",
                CardId: ReadGuid(root, "cardId"), Drawer: ReadString(root, "drawer")));
        }
        else if (type == "reset") events.Enqueue(new RelayGameEvent(RelayGameEventType.Reset, "The host shuffled and reset the relay deck."));
        else if (type == "game-started") events.Enqueue(new RelayGameEvent(RelayGameEventType.GameStarted, $"Relay game started. {ReadString(root, "currentPlayer")} draws first."));
        else if (type == "volunteer-prompt") events.Enqueue(new RelayGameEvent(RelayGameEventType.VolunteerPrompt,
            $"{ReadString(root, "drawer")} needs a blind volunteer.", ResolutionId: ReadGuid(root, "resolutionId"),
            Drawer: ReadString(root, "drawer"), DeadlineUnixMilliseconds: ReadInt64(root, "deadline")));
        else if (type == "volunteer-resolved") events.Enqueue(new RelayGameEvent(RelayGameEventType.VolunteerResolved,
            $"{ReadString(root, "selectedPlayer")} is the blind volunteer.", CardId: ReadGuid(root, "cardId"),
            ResolutionId: ReadGuid(root, "resolutionId"), Drawer: ReadString(root, "drawer"), SelectedPlayer: ReadString(root, "selectedPlayer")));
        else if (type == "random-target") events.Enqueue(new RelayGameEvent(RelayGameEventType.RandomTargetSelected,
            $"{ReadString(root, "selectedPlayer")} was randomly chosen for {ReadString(root, "drawer")}'s card.",
            Drawer: ReadString(root, "drawer"), SelectedPlayer: ReadString(root, "selectedPlayer")));
        else if (type == "game-ended") events.Enqueue(new RelayGameEvent(RelayGameEventType.GameEnded, "The relay game ended.",
            Scores: root.TryGetProperty("scores", out var scores) ? scores.Deserialize<Dictionary<string, int>>(JsonOptions) : null,
            Winners: root.TryGetProperty("winners", out var winners) ? winners.Deserialize<List<string>>(JsonOptions) : null));
        else if (type == "tie-break-started") events.Enqueue(new RelayGameEvent(RelayGameEventType.TieBreakStarted,
            "The first-place tie requires a deciding vote."));
        else if (type == "room-closed") events.Enqueue(new RelayGameEvent(RelayGameEventType.RoomClosed,
            root.TryGetProperty("message", out var closedMessage) ? closedMessage.GetString() ?? "The relay room closed." : "The relay room closed."));
        else if (type == "deck-ready") events.Enqueue(new RelayGameEvent(RelayGameEventType.Status, "The host deck is ready."));
        else if (type == "error") events.Enqueue(new RelayGameEvent(RelayGameEventType.Error,
            root.TryGetProperty("message", out var message) ? message.GetString() ?? "Relay error." : "Relay error."));
        else events.Enqueue(new RelayGameEvent(RelayGameEventType.Message, "Relay game message received.", Payload: root.Clone()));
    }

    private static string ReadString(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    private static Guid? ReadGuid(JsonElement root, string name) => Guid.TryParse(ReadString(root, name), out var value) ? value : null;
    private static long ReadInt64(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static async Task<RelayJoinResponse> ReadJoin(HttpResponseMessage response, CancellationToken token) =>
        await JsonSerializer.DeserializeAsync<RelayJoinResponse>(await response.Content.ReadAsStreamAsync(token), JsonOptions, token)
        ?? throw new InvalidDataException("The relay returned an empty room response.");

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            var parsed = JsonSerializer.Deserialize<RelayError>(body, JsonOptions);
            throw new InvalidOperationException(parsed?.Error ?? $"Relay request failed ({(int)response.StatusCode}).");
        }
        catch (JsonException) { throw new InvalidOperationException($"Relay request failed ({(int)response.StatusCode})."); }
    }

    private string Endpoint() { lock (stateLock) return endpoint; }
    private void SetBusy(bool value) { lock (stateLock) busy = value; }
    private void QueueError(string message, Exception ex) => events.Enqueue(new RelayGameEvent(RelayGameEventType.Error, $"{message} {ex.Message}"));
    public void Dispose() { LeaveAsync().GetAwaiter().GetResult(); http.Dispose(); }

    private sealed record RelayJoinResponse(RelayRoomSummary Room, string SeatToken, string ReconnectToken, string? HostToken);
    private sealed record RelayError(string Error);
}

public sealed record RelayRoomSummary(string Code, string Name, string Host, string Visibility, IReadOnlyList<string> Intensity,
    bool PasswordProtected, int Players, int Capacity, bool Started, long ExpiresAt, bool DeckReady = false,
    string? DeckHash = null, long? DeckBytes = null);
public sealed record RelayPlayer(string Id, string Name, bool Host, bool Connected);
public sealed record RelayCardDefinition(Guid Id, string Keyword);
public sealed record RelayPendingVolunteer(Guid ResolutionId, string Drawer, long Deadline);
public sealed record RelayGameState(bool Started, string CurrentPlayer, IReadOnlyList<string> TurnOrder, int Remaining,
    bool ScoringEnabled, IReadOnlyDictionary<string, int> Scores, string ScoringDrawer,
    IReadOnlyList<string> EligibleVoters, IReadOnlyList<string> SubmittedVoters, Guid? CurrentCardId,
    string? CurrentDrawer, string? RandomTarget, RelayPendingVolunteer? PendingVolunteer,
    IReadOnlyList<string> TieBreakCandidates, IReadOnlyList<string> EligibleTieVoters,
    IReadOnlyList<string> SubmittedTieVoters);
public sealed record RelayGameEvent(RelayGameEventType Type, string Message, byte[]? Bundle = null,
    RelayRoomSummary? Room = null, JsonElement? Payload = null, Guid? CardId = null, string? Drawer = null,
    Guid? ResolutionId = null, string? SelectedPlayer = null, long DeadlineUnixMilliseconds = 0,
    IReadOnlyDictionary<string, int>? Scores = null, IReadOnlyList<string>? Winners = null);
public enum RelayGameEventType { Status, Error, RoomListChanged, RoomJoined, DeckReceived, PlayersChanged, Message,
    GameStateChanged, CardDrawn, Reset, GameStarted, VolunteerPrompt, VolunteerResolved, RandomTargetSelected,
    TieBreakStarted, GameEnded, RoomClosed }
