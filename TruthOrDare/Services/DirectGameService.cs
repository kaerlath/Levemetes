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
    private readonly Dictionary<Guid, CardKeyword?> cardKeywords = [];
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
    private PendingVolunteer? pendingVolunteer;
    private bool scoringEnabled;
    private string scoringDrawer = string.Empty;
    private readonly Dictionary<string, int> scores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> roundVotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> eligibleScoreVoters = new(StringComparer.OrdinalIgnoreCase);
    private int eligibleScoreVoterCount;
    private readonly List<string> tieBreakCandidates = [];
    private readonly HashSet<string> eligibleTieBreakVoters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> tieBreakVotes = new(StringComparer.OrdinalIgnoreCase);

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
    public bool ScoringEnabled { get { lock (stateLock) return scoringEnabled; } }
    public bool AwaitingScores { get { lock (stateLock) return scoringEnabled && scoringDrawer.Length > 0; } }
    public string ScoringDrawer { get { lock (stateLock) return scoringDrawer; } }
    public IReadOnlyDictionary<string, int> Scores { get { lock (stateLock) return new Dictionary<string, int>(scores); } }
    public IReadOnlyCollection<string> SubmittedVoters { get { lock (stateLock) return roundVotes.Keys.ToArray(); } }
    public IReadOnlyCollection<string> EligibleScoreVoters { get { lock (stateLock) return eligibleScoreVoters.ToArray(); } }
    public int EligibleScoreVoterCount { get { lock (stateLock) return eligibleScoreVoterCount; } }
    public IReadOnlyList<string> TieBreakCandidates { get { lock (stateLock) return tieBreakCandidates.ToArray(); } }
    public IReadOnlyCollection<string> SubmittedTieBreakVoters { get { lock (stateLock) return tieBreakVotes.Keys.ToArray(); } }
    public bool AwaitingTieBreak { get { lock (stateLock) return tieBreakCandidates.Count > 0; } }

    public static async Task<string> DiscoverPublicAddressAsync(CancellationToken token = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var value = (await client.GetStringAsync("https://api.ipify.org", token)).Trim();
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("The public-address service did not return a valid IPv4 address.");
        return value;
    }

    public void StartHosting(string name, string publicAddress, int listenPort, Deck deck, CardCategory selectedCategory, byte[] deckBundle, bool enableScoring = false)
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
        scoringEnabled = enableScoring;
        scores.Clear(); scores[name] = 0; roundVotes.Clear(); scoringDrawer = string.Empty;
        cardKeywords.Clear();
        foreach (var card in deck.Cards) cardKeywords[card.Id] = card.Keyword;
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

    public void SubmitScore(int value)
    {
        if (value is < 0 or > 5) throw new InvalidOperationException("Scores must be between 0 and 5.");
        if (IsHost) HostScore(playerName, value);
        else if (Mode == DirectGameMode.Joined && hostPeer is not null)
            _ = hostPeer.SendJsonAsync(PacketType.ScoreRequest, new ScoreRequest(value), cancellation?.Token ?? CancellationToken.None);
    }

    public void ForcePassScores()
    {
        if (!IsHost) throw new InvalidOperationException("Only the host can force-pass scoring.");

        ScoreStateNotice notice;
        var forcedCount = 0;
        lock (stateLock)
        {
            if (!scoringEnabled || scoringDrawer.Length == 0) return;

            foreach (var voter in eligibleScoreVoters.Where(voter => !roundVotes.ContainsKey(voter)))
            {
                roundVotes[voter] = 3;
                forcedCount++;
            }

            if (forcedCount == 0) return;

            scores.TryAdd(scoringDrawer, 0);
            scores[scoringDrawer] += roundVotes.Values.Sum();
            scoringDrawer = string.Empty;
            roundVotes.Clear();
            eligibleScoreVoters.Clear();
            eligibleScoreVoterCount = 0;
            AdvanceTurnLocked();
            notice = MakeScoreStateLocked();
        }

        BroadcastJson(PacketType.ScoreState, notice);
        events.Enqueue(new DirectGameEvent(DirectGameEventType.ScoreStateChanged,
            $"The host force-passed {forcedCount} missing vote{(forcedCount == 1 ? string.Empty : "s")} at 3 points."));
        BroadcastGameState();
    }

    public void SubmitTieBreakVote(string candidate)
    {
        if (IsHost) HostTieBreakVote(playerName, candidate);
        else if (Mode == DirectGameMode.Joined && hostPeer is not null)
            _ = hostPeer.SendJsonAsync(PacketType.TieBreakVote, new TieBreakVoteRequest(candidate), cancellation?.Token ?? CancellationToken.None);
    }

    public void EndGame()
    {
        if (!IsHost) throw new InvalidOperationException("Only the host can end the game.");
        GameResultsNotice? result = null;
        TieBreakStateNotice? tieBreak = null;
        lock (stateLock)
        {
            gameStarted = false;
            scoringDrawer = string.Empty;
            eligibleScoreVoters.Clear();
            eligibleScoreVoterCount = 0;
            if (!scoringEnabled)
            {
                result = new GameResultsNotice(new Dictionary<string, int>(), []);
                tieBreakCandidates.Clear(); eligibleTieBreakVoters.Clear(); tieBreakVotes.Clear();
                goto FinishedEnding;
            }
            var best = scores.Count == 0 ? 0 : scores.Values.Max();
            var leaders = scores.Where(pair => pair.Value == best).Select(pair => pair.Key).ToArray();
            var voters = ConnectedNamesLocked().Where(name => !leaders.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (scoringEnabled && leaders.Length > 1 && voters.Length > 0)
            {
                tieBreakCandidates.Clear(); tieBreakCandidates.AddRange(leaders);
                eligibleTieBreakVoters.Clear(); eligibleTieBreakVoters.UnionWith(voters);
                tieBreakVotes.Clear();
                tieBreak = MakeTieBreakStateLocked();
            }
            else result = MakeGameResultsLocked(leaders);
        FinishedEnding:;
        }
        if (tieBreak is not null)
        {
            BroadcastJson(PacketType.TieBreakState, tieBreak);
            events.Enqueue(new DirectGameEvent(DirectGameEventType.TieBreakStarted, "A first-place tie needs a deciding vote."));
        }
        else if (result is not null) PublishGameResults(result);
        BroadcastGameState();
    }

    public void Volunteer(Guid resolutionId)
    {
        if (IsHost) ResolveVolunteer(resolutionId, playerName, false);
        else if (Mode == DirectGameMode.Joined && hostPeer is not null)
            _ = hostPeer.SendJsonAsync(PacketType.VolunteerRequest, new VolunteerRequest(resolutionId, playerName), cancellation?.Token ?? CancellationToken.None);
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
            foreach (var name in names) scores[name] = 0;
            scoringDrawer = string.Empty; roundVotes.Clear(); eligibleScoreVoters.Clear(); eligibleScoreVoterCount = 0;
            tieBreakCandidates.Clear(); eligibleTieBreakVoters.Clear(); tieBreakVotes.Clear();
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
        ReconcilePlayerDeparture(name);
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
            cardKeywords.Clear();
            pendingVolunteer = null;
            turnOrder.Clear();
            gameStarted = false;
            currentTurnIndex = -1;
            scoringEnabled = false; scoringDrawer = string.Empty; scores.Clear(); roundVotes.Clear(); eligibleScoreVoters.Clear(); eligibleScoreVoterCount = 0;
            tieBreakCandidates.Clear(); eligibleTieBreakVoters.Clear(); tieBreakVotes.Clear();
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
                scores.TryAdd(peer.Name, 0);
                UpdatePlayerNamesLocked();
            }
            await peer.SendJsonAsync(PacketType.Welcome, new Welcome(category, remaining, scoringEnabled), token);
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
                ReconcilePlayerDeparture(peer.Name);
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
            else if (packet.Type == PacketType.ScoreRequest) HostScore(peer.Name, Deserialize<ScoreRequest>(packet.Payload).Value);
            else if (packet.Type == PacketType.TieBreakVote) HostTieBreakVote(peer.Name, Deserialize<TieBreakVoteRequest>(packet.Payload).Candidate);
            else if (packet.Type == PacketType.VolunteerRequest)
            {
                var request = Deserialize<VolunteerRequest>(packet.Payload);
                ResolveVolunteer(request.ResolutionId, peer.Name, false);
            }
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
            lock (stateLock) { category = welcome.Category; remaining = welcome.Remaining; scoringEnabled = welcome.ScoringEnabled; mode = DirectGameMode.Joined; }
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
                    case PacketType.VolunteerPrompt:
                        var prompt = Deserialize<VolunteerPrompt>(packet.Payload);
                        events.Enqueue(new DirectGameEvent(DirectGameEventType.VolunteerPrompt,
                            $"{prompt.Drawer} drew a BLIND VOLUNTEER card.", ResolutionId: prompt.ResolutionId,
                            DeadlineUnixMilliseconds: prompt.DeadlineUnixMilliseconds, Drawer: prompt.Drawer));
                        break;
                    case PacketType.VolunteerResolved:
                        var resolution = Deserialize<VolunteerResolved>(packet.Payload);
                        events.Enqueue(new DirectGameEvent(DirectGameEventType.VolunteerResolved,
                            resolution.WasAutomatic
                                ? $"No one volunteered. {resolution.SelectedPlayer} was randomly chosen as the blind volunteer."
                                : $"{resolution.SelectedPlayer} volunteered as the blind volunteer.",
                            CardId: resolution.CardId, Drawer: resolution.Drawer, Remaining: resolution.Remaining,
                            ResolutionId: resolution.ResolutionId, SelectedPlayer: resolution.SelectedPlayer));
                        break;
                    case PacketType.RandomTarget:
                        var target = Deserialize<RandomTargetNotice>(packet.Payload);
                        events.Enqueue(new DirectGameEvent(DirectGameEventType.RandomTargetSelected,
                            $"{target.SelectedPlayer} was randomly chosen for {target.Drawer}'s card.", SelectedPlayer: target.SelectedPlayer));
                        break;
                    case PacketType.ScoreState:
                        ApplyScoreState(Deserialize<ScoreStateNotice>(packet.Payload));
                        break;
                    case PacketType.GameResults:
                        var results = Deserialize<GameResultsNotice>(packet.Payload);
                        ApplyGameResults(results);
                        break;
                    case PacketType.TieBreakState:
                        ApplyTieBreakState(Deserialize<TieBreakStateNotice>(packet.Payload));
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
            if (pendingVolunteer is not null) { SendDrawError(drawer, "Resolve the current BLIND VOLUNTEER card before drawing again."); return; }
            if (scoringEnabled && scoringDrawer.Length > 0) { SendDrawError(drawer, "Wait for all eligible players to submit a score."); return; }
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
            if (!scoringEnabled) AdvanceTurnLocked();
        }
        var result = new DrawResult(Guid.NewGuid(), cardId, drawer, count);
        cardKeywords.TryGetValue(cardId, out var keyword);
        if (keyword == CardKeyword.BlindVolunteer)
        {
            StartBlindVolunteer(result);
        }
        else
        {
            events.Enqueue(new DirectGameEvent(DirectGameEventType.CardDrawn, $"{drawer} drew a card.", CardId: cardId, Drawer: drawer, Remaining: count));
            BroadcastJson(PacketType.DrawResult, result);
            if (keyword == CardKeyword.Random) ResolveRandomTarget(drawer);
            if (scoringEnabled) BeginScoring(drawer);
        }
        BroadcastGameState();
    }

    private void HostScore(string voter, int value)
    {
        ScoreStateNotice notice;
        lock (stateLock)
        {
            if (!scoringEnabled || scoringDrawer.Length == 0) return;
            if (voter.Equals(scoringDrawer, StringComparison.OrdinalIgnoreCase) || roundVotes.ContainsKey(voter)) return;
            if (!eligibleScoreVoters.Contains(voter)) return;
            roundVotes[voter] = Math.Clamp(value, 0, 5);
            if (eligibleScoreVoters.All(name => roundVotes.ContainsKey(name)))
            {
                scores.TryAdd(scoringDrawer, 0);
                scores[scoringDrawer] += roundVotes.Values.Sum();
                scoringDrawer = string.Empty;
                roundVotes.Clear();
                eligibleScoreVoters.Clear();
                eligibleScoreVoterCount = 0;
                AdvanceTurnLocked();
            }
            notice = MakeScoreStateLocked();
        }
        BroadcastJson(PacketType.ScoreState, notice);
        events.Enqueue(new DirectGameEvent(DirectGameEventType.ScoreStateChanged, notice.Drawer.Length == 0 ? "Scoring complete." : "Waiting for scores."));
        BroadcastGameState();
    }

    private void HostTieBreakVote(string voter, string candidate)
    {
        GameResultsNotice? result = null;
        TieBreakStateNotice? state = null;
        lock (stateLock)
        {
            if (tieBreakCandidates.Count == 0 || !eligibleTieBreakVoters.Contains(voter) || tieBreakVotes.ContainsKey(voter)) return;
            if (!tieBreakCandidates.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return;
            tieBreakVotes[voter] = candidate;
            if (eligibleTieBreakVoters.All(name => tieBreakVotes.ContainsKey(name)))
            {
                var counts = tieBreakVotes.Values.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
                var highest = counts.Values.Max();
                var winners = tieBreakCandidates.Where(name => counts.GetValueOrDefault(name) == highest).ToArray();
                result = MakeGameResultsLocked(winners);
                tieBreakCandidates.Clear(); eligibleTieBreakVoters.Clear(); tieBreakVotes.Clear();
            }
            else state = MakeTieBreakStateLocked();
        }
        if (result is not null) PublishGameResults(result);
        else if (state is not null)
        {
            BroadcastJson(PacketType.TieBreakState, state);
            events.Enqueue(new DirectGameEvent(DirectGameEventType.TieBreakStarted, "Waiting for the remaining tie-break votes."));
        }
    }

    private void ReconcilePlayerDeparture(string name)
    {
        ScoreStateNotice? scoreState = null;
        GameResultsNotice? result = null;
        lock (stateLock)
        {
            if (eligibleScoreVoters.Remove(name))
            {
                eligibleScoreVoterCount = eligibleScoreVoters.Count;
                roundVotes.Remove(name);
                if (scoringDrawer.Length > 0 && eligibleScoreVoters.All(voter => roundVotes.ContainsKey(voter)))
                {
                    scores[scoringDrawer] = scores.GetValueOrDefault(scoringDrawer) + roundVotes.Values.Sum();
                    scoringDrawer = string.Empty; roundVotes.Clear(); eligibleScoreVoters.Clear(); eligibleScoreVoterCount = 0;
                    AdvanceTurnLocked();
                }
                scoreState = MakeScoreStateLocked();
            }
            if (eligibleTieBreakVoters.Remove(name))
            {
                tieBreakVotes.Remove(name);
                if (eligibleTieBreakVoters.Count == 0 || eligibleTieBreakVoters.All(voter => tieBreakVotes.ContainsKey(voter)))
                {
                    var winners = ResolveTieBreakWinnersLocked();
                    result = MakeGameResultsLocked(winners);
                    tieBreakCandidates.Clear(); eligibleTieBreakVoters.Clear(); tieBreakVotes.Clear();
                }
            }
        }
        if (scoreState is not null)
        {
            BroadcastJson(PacketType.ScoreState, scoreState);
            events.Enqueue(new DirectGameEvent(DirectGameEventType.ScoreStateChanged, scoreState.Drawer.Length == 0 ? "Scoring complete." : "Waiting for scores."));
            BroadcastGameState();
        }
        if (result is not null) PublishGameResults(result);
    }

    private string[] ResolveTieBreakWinnersLocked()
    {
        if (tieBreakVotes.Count == 0) return tieBreakCandidates.ToArray();
        var counts = tieBreakVotes.Values.GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var highest = counts.Values.Max();
        return tieBreakCandidates.Where(name => counts.GetValueOrDefault(name) == highest).ToArray();
    }

    private TieBreakStateNotice MakeTieBreakStateLocked() => new(tieBreakCandidates.ToArray(), tieBreakVotes.Keys.ToArray(), eligibleTieBreakVoters.Count);
    private GameResultsNotice MakeGameResultsLocked(IReadOnlyList<string> winners) => new(new Dictionary<string, int>(scores), winners);
    private void ApplyTieBreakState(TieBreakStateNotice state)
    {
        lock (stateLock)
        {
            tieBreakCandidates.Clear(); tieBreakCandidates.AddRange(state.Candidates);
            tieBreakVotes.Clear(); foreach (var voter in state.Submitted) tieBreakVotes[voter] = string.Empty;
            eligibleTieBreakVoters.Clear();
        }
        events.Enqueue(new DirectGameEvent(DirectGameEventType.TieBreakStarted, "A first-place tie needs a deciding vote."));
    }

    private void PublishGameResults(GameResultsNotice result)
    {
        BroadcastJson(PacketType.GameResults, result);
        ApplyGameResults(result);
    }

    private void ApplyGameResults(GameResultsNotice result)
    {
        lock (stateLock) { tieBreakCandidates.Clear(); eligibleTieBreakVoters.Clear(); tieBreakVotes.Clear(); }
        events.Enqueue(new DirectGameEvent(DirectGameEventType.GameEnded, "The host ended the game.", Scores: result.Scores, Winners: result.Winners));
    }

    private IEnumerable<string> ConnectedNamesLocked() => new[] { playerName }.Concat(peers.Select(peer => peer.Name));
    private void BeginScoring(string drawer)
    {
        ScoreStateNotice notice;
        lock (stateLock)
        {
            scoringDrawer = drawer;
            roundVotes.Clear();
            eligibleScoreVoters.Clear();
            eligibleScoreVoters.UnionWith(ConnectedNamesLocked().Where(name => !name.Equals(drawer, StringComparison.OrdinalIgnoreCase)));
            eligibleScoreVoterCount = eligibleScoreVoters.Count;
            if (eligibleScoreVoters.Count == 0) { scoringDrawer = string.Empty; AdvanceTurnLocked(); }
            notice = MakeScoreStateLocked();
        }
        BroadcastJson(PacketType.ScoreState, notice);
        events.Enqueue(new DirectGameEvent(DirectGameEventType.ScoreStateChanged, notice.Drawer.Length == 0 ? "Scoring complete." : "Waiting for scores."));
        BroadcastGameState();
    }

    private ScoreStateNotice MakeScoreStateLocked() => new(scoringEnabled, scoringDrawer, new Dictionary<string, int>(scores), roundVotes.Keys.ToArray(), eligibleScoreVoters.ToArray());
    private void BroadcastScoreState() => BroadcastJson(PacketType.ScoreState, MakeScoreStateLocked());
    private void ApplyScoreState(ScoreStateNotice state)
    {
        lock (stateLock) { scoringEnabled = state.Enabled; scoringDrawer = state.Drawer; scores.Clear(); foreach (var pair in state.Scores) scores[pair.Key] = pair.Value; roundVotes.Clear(); foreach (var name in state.Submitted) roundVotes[name] = 0; eligibleScoreVoters.Clear(); foreach (var name in state.Eligible) eligibleScoreVoters.Add(name); eligibleScoreVoterCount = eligibleScoreVoters.Count; }
        events.Enqueue(new DirectGameEvent(DirectGameEventType.ScoreStateChanged, scoringDrawer.Length == 0 ? "Scoring complete." : "Waiting for scores."));
    }

    private void StartBlindVolunteer(DrawResult draw)
    {
        var resolutionId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds();
        List<Peer> otherPeers;
        lock (stateLock)
        {
            pendingVolunteer = new PendingVolunteer(resolutionId, draw.CardId, draw.Drawer, draw.Remaining, deadline);
            otherPeers = peers.Where(peer => !peer.Name.Equals(draw.Drawer, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (draw.Drawer.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            events.Enqueue(new DirectGameEvent(DirectGameEventType.CardDrawn, $"{draw.Drawer} drew a BLIND VOLUNTEER card.", CardId: draw.CardId, Drawer: draw.Drawer, Remaining: draw.Remaining));
        else
        {
            var drawerPeer = peers.FirstOrDefault(peer => peer.Name.Equals(draw.Drawer, StringComparison.OrdinalIgnoreCase));
            if (drawerPeer is not null) _ = drawerPeer.SendJsonAsync(PacketType.DrawResult, draw, cancellation?.Token ?? CancellationToken.None);
        }

        var prompt = new VolunteerPrompt(resolutionId, draw.Drawer, deadline);
        foreach (var peer in otherPeers) _ = peer.SendJsonAsync(PacketType.VolunteerPrompt, prompt, cancellation?.Token ?? CancellationToken.None);
        if (!draw.Drawer.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            events.Enqueue(new DirectGameEvent(DirectGameEventType.VolunteerPrompt, $"{draw.Drawer} drew a BLIND VOLUNTEER card.",
                ResolutionId: resolutionId, DeadlineUnixMilliseconds: deadline, Drawer: draw.Drawer));
        _ = ResolveVolunteerAfterTimeoutAsync(resolutionId, cancellation?.Token ?? CancellationToken.None);
    }

    private async Task ResolveVolunteerAfterTimeoutAsync(Guid resolutionId, CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), token); ResolveVolunteer(resolutionId, null, true); }
        catch (OperationCanceledException) { }
    }

    private void ResolveVolunteer(Guid resolutionId, string? volunteer, bool automatic)
    {
        VolunteerResolved result;
        lock (stateLock)
        {
            if (pendingVolunteer is not { } pending || pending.ResolutionId != resolutionId) return;
            var eligible = peers.Select(peer => peer.Name).Append(playerName)
                .Where(name => !name.Equals(pending.Drawer, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!automatic)
            {
                if (volunteer is null || !eligible.Contains(volunteer, StringComparer.OrdinalIgnoreCase)) return;
            }
            else
            {
                if (eligible.Count == 0)
                {
                    pendingVolunteer = null;
                    events.Enqueue(new DirectGameEvent(DirectGameEventType.Error, "No other connected player was available for BLIND VOLUNTEER."));
                    return;
                }
                volunteer = eligible[RandomNumberGenerator.GetInt32(eligible.Count)];
            }
            pendingVolunteer = null;
            result = new VolunteerResolved(resolutionId, pending.CardId, pending.Drawer, pending.Remaining, volunteer!, automatic);
        }
        BroadcastJson(PacketType.VolunteerResolved, result);
        events.Enqueue(new DirectGameEvent(DirectGameEventType.VolunteerResolved,
            automatic ? $"No one volunteered. {result.SelectedPlayer} was randomly chosen as the blind volunteer."
                : $"{result.SelectedPlayer} volunteered as the blind volunteer.",
            CardId: result.CardId, Drawer: result.Drawer, Remaining: result.Remaining,
            ResolutionId: resolutionId, SelectedPlayer: result.SelectedPlayer));
        if (scoringEnabled) BeginScoring(result.Drawer);
    }

    private void ResolveRandomTarget(string drawer)
    {
        List<string> eligible;
        lock (stateLock) eligible = peers.Select(peer => peer.Name).Append(playerName)
            .Where(name => !name.Equals(drawer, StringComparison.OrdinalIgnoreCase)).ToList();
        if (eligible.Count == 0)
        {
            events.Enqueue(new DirectGameEvent(DirectGameEventType.Error, "No other connected player was available for the RANDOM card."));
            return;
        }
        var selected = eligible[RandomNumberGenerator.GetInt32(eligible.Count)];
        var notice = new RandomTargetNotice(drawer, selected);
        BroadcastJson(PacketType.RandomTarget, notice);
        events.Enqueue(new DirectGameEvent(DirectGameEventType.RandomTargetSelected, $"{selected} was randomly chosen for {drawer}'s card.", SelectedPlayer: selected));
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

    private enum PacketType : byte { Join = 1, Welcome, DeckBundle, DrawRequest, DrawResult, PlayerList, Reset, Error, GameState, VolunteerRequest, VolunteerPrompt, VolunteerResolved, RandomTarget, ScoreRequest, ScoreState, GameResults, TieBreakVote, TieBreakState }
    private sealed record Invite(string Host, int Port, Guid SessionId, string Secret);
    private sealed record JoinRequest(string PlayerName);
    private sealed record Welcome(CardCategory Category, int Remaining, bool ScoringEnabled);
    private sealed record DrawRequest(string PlayerName);
    private sealed record DrawResult(Guid DrawId, Guid CardId, string Drawer, int Remaining);
    private sealed record PlayerListNotice(IReadOnlyList<string> Names);
    private sealed record ResetNotice(int Remaining);
    private sealed record ErrorNotice(string Message);
    private sealed record GameStateNotice(bool Started, IReadOnlyList<string> TurnOrder, int CurrentTurnIndex, string CurrentPlayer);
    private sealed record VolunteerRequest(Guid ResolutionId, string PlayerName);
    private sealed record VolunteerPrompt(Guid ResolutionId, string Drawer, long DeadlineUnixMilliseconds);
    private sealed record VolunteerResolved(Guid ResolutionId, Guid CardId, string Drawer, int Remaining, string SelectedPlayer, bool WasAutomatic);
    private sealed record RandomTargetNotice(string Drawer, string SelectedPlayer);
    private sealed record ScoreRequest(int Value);
    private sealed record ScoreStateNotice(bool Enabled, string Drawer, IReadOnlyDictionary<string, int> Scores, IReadOnlyList<string> Submitted, IReadOnlyList<string> Eligible);
    private sealed record TieBreakVoteRequest(string Candidate);
    private sealed record TieBreakStateNotice(IReadOnlyList<string> Candidates, IReadOnlyList<string> Submitted, int EligibleCount);
    private sealed record GameResultsNotice(IReadOnlyDictionary<string, int> Scores, IReadOnlyList<string> Winners);
    private sealed record PendingVolunteer(Guid ResolutionId, Guid CardId, string Drawer, int Remaining, long DeadlineUnixMilliseconds);
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
public enum DirectGameEventType { Status, Error, DeckReceived, CardDrawn, Reset, PlayerListChanged, GameStarted, GameStateChanged, VolunteerPrompt, VolunteerResolved, RandomTargetSelected, ScoreStateChanged, TieBreakStarted, GameEnded }
public sealed record DirectGameEvent(DirectGameEventType Type, string Message, byte[]? Bundle = null, Guid? CardId = null,
    string? Drawer = null, int Remaining = 0, CardCategory Category = CardCategory.None, Guid? ResolutionId = null,
    long DeadlineUnixMilliseconds = 0, string? SelectedPlayer = null, IReadOnlyDictionary<string, int>? Scores = null,
    IReadOnlyList<string>? Winners = null);
