using Dalamud.Interface.Colors;
using LaciSynchroni.Common.Data;
using LaciSynchroni.Common.Dto;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Events;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration;
using LaciSynchroni.Utils;
using LaciSynchroni.WebAPI.SignalR.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace LaciSynchroni.WebAPI;
using ServerIndex = int;

#pragma warning disable MA0040
public sealed partial class ApiController : DisposableMediatorSubscriberBase
{
    public const string MainServer = "Laci Synchroni";
    public const string MainServiceUri = "wss://sinus.syrilai.dev";

    private readonly DalamudUtilService _dalamudUtil;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILoggerProvider _loggerProvider;
    private readonly MultiConnectTokenService _multiConnectTokenService;
    private readonly SyncConfigService _syncConfigService;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// In preparation for multi-connect, all the SignalR connection functionality
    /// has been moved into an instantiated, not injected, client.
    /// That client, internally, uses the server index to access the server object, and everything is based on that index.
    /// No more usage of ServerConfigurationManager#Current server
    /// Down the line, we move this into a list or a Map of active servers.
    /// </summary>
    private readonly ConcurrentDictionary<ServerIndex, SyncHubClient> _syncHubClients = new();

    public ApiController(ILogger<ApiController> logger, ILoggerFactory loggerFactory, DalamudUtilService dalamudUtil, ILoggerProvider loggerProvider,
        PairManager pairManager, ServerConfigurationManager serverConfigManager, SyncMediator mediator, MultiConnectTokenService multiConnectTokenService, SyncConfigService syncConfigService, HttpClient httpClient) : base(logger, mediator)
    {
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _serverConfigManager = serverConfigManager;
        _multiConnectTokenService = multiConnectTokenService;
        _syncConfigService = syncConfigService;
        _httpClient = httpClient;
        _loggerFactory = loggerFactory;
        _loggerProvider = loggerProvider;
        
        // When we log out, we could either:
        // - Disconnect all clients
        // - Dispose all clients
        // It seems wise to just discard everything instead of disconnecting them. If we just disconnect them, it might
        // take a tad longer in case of network errors. Potentially, that causes a reconnect during the next login,
        // which might get weird!
        // Better to just throw them away and recreate if needed from scratch!
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => DisposeAllClients());
        // We get the login message both when:
        // - the plugin framework updates the first time and the user is logged in
        // - the user manually logged in
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) => AutoConnectClients());
    }

    public string GetServerErrorByServer(int serverId)
    {
        var authFailureMessage = GetAuthFailureMessageByServer(serverId);
        return GetServerErrorByState(GetServerStateForServer(serverId), authFailureMessage);
    }

    public static string GetServerErrorByState(ServerState state, string? authFailureMessage)
    {
        return state switch
        {
            ServerState.Connecting => "Attempting to connect to the server.",
            ServerState.Reconnecting => "Connection to server interrupted, attempting to reconnect to the server.",
            ServerState.Disconnected => "You are currently disconnected from this server.",
            ServerState.Disconnecting => "Disconnecting from the server",
            ServerState.Unauthorized => "Server Response: " + authFailureMessage,
            ServerState.Offline => "This server is currently offline.",
            ServerState.VersionMisMatch =>
                "Your plugin or the server you are connecting to is out of date. Please update your plugin now. If you already did so, contact the server provider to update their server to the latest version.",
            ServerState.RateLimited => "You are rate limited for (re)connecting too often. Disconnect, wait 10 minutes and try again.",
            ServerState.Connected => string.Empty,
            ServerState.NoSecretKey => "You have no secret key set for this current character. Open Settings -> Service Settings and set a secret key for the current character. You can reuse the same secret key for multiple characters.",
            ServerState.MultiChara => "Your Character Configuration has multiple characters configured with same name and world. You will not be able to connect until you fix this issue. Remove the duplicates from the configuration in Settings -> Service Settings -> Character Management and reconnect manually after.",
            ServerState.OAuthMisconfigured => "OAuth2 is enabled but not fully configured, verify in the Settings -> Service Settings that you have OAuth2 connected and, importantly, a UID assigned to your current character.",
            ServerState.OAuthLoginTokenStale => "Your OAuth2 login token is stale and cannot be used to renew. Go to the Settings -> Service Settings and unlink then relink your OAuth2 configuration.",
            ServerState.NoAutoLogon => "This character has automatic login disabled for the server. Press the connect button to connect to a server.",
            ServerState.NoHubFound => "Sync Hub not found. Please request the correct Hub URI from the person running the server you want to connect to.",
            _ => string.Empty
        };
    }

    public Vector4 GetUidColorByServer(int serverId)
    {
        return GetUidColorByState(GetServerStateForServer(serverId));
    }

    public static Vector4 GetUidColorByState(ServerState state)
    {
        return state switch
        {
            ServerState.Connecting => ImGuiColors.DalamudYellow,
            ServerState.Reconnecting => ImGuiColors.DalamudRed,
            ServerState.Connected => ImGuiColors.ParsedGreen,
            ServerState.Disconnected => ImGuiColors.DalamudYellow,
            ServerState.Disconnecting => ImGuiColors.DalamudYellow,
            ServerState.Unauthorized => ImGuiColors.DalamudRed,
            ServerState.VersionMisMatch => ImGuiColors.DalamudRed,
            ServerState.Offline => ImGuiColors.DalamudRed,
            ServerState.RateLimited => ImGuiColors.DalamudYellow,
            ServerState.NoSecretKey => ImGuiColors.DalamudYellow,
            ServerState.MultiChara => ImGuiColors.DalamudYellow,
            ServerState.OAuthMisconfigured => ImGuiColors.DalamudRed,
            ServerState.OAuthLoginTokenStale => ImGuiColors.DalamudRed,
            ServerState.NoAutoLogon => ImGuiColors.DalamudYellow,
            _ => ImGuiColors.DalamudRed
        };
    }

    public ServerState GetServerStateForServer(ServerIndex index)
    {
        return GetClientForServer(index)?.ServerState ?? ServerState.Offline;
    }

    public bool IsServerConnected(ServerIndex index)
    {
        return GetClientForServer(index)?.ServerState == ServerState.Connected;
    }

    public bool IsServerConnectingOrConnected(int index)
    {
        var serverState = GetClientForServer(index)?.ServerState;
        return serverState is (ServerState.Connected or ServerState.Connecting or ServerState.Reconnecting);
    }

    public string GetServerNameByIndex(ServerIndex index)
    {
        return _serverConfigManager.GetServerByIndex(index).ServerName ?? string.Empty;
    }

    public int GetOnlineUsersForServer(ServerIndex index)
    {
        return GetClientForServer(index)?.SystemInfoDto?.OnlineUsers ?? 0;
    }

    public bool IsServerAlive(int index)
    {
        var serverState = GetServerStateForServer(index);
        return serverState is ServerState.Connected or ServerState.RateLimited
            or ServerState.Unauthorized or ServerState.Disconnected;
    }

    public int OnlineUsers
    {
        get
        {
            return _syncHubClients.Sum(entry => entry.Value.SystemInfoDto?.OnlineUsers ?? 0);
        }
    }

    public ServerInfo? GetServerInfoForServer(ServerIndex index)
    {
        return GetClientForServer(index)?.ConnectionDto?.ServerInfo;
    }

    public DefaultPermissionsDto? GetDefaultPermissionsForServer(ServerIndex index)
    {
        return GetClientForServer(index)?.ConnectionDto?.DefaultPreferredPermissions;
    }

    public bool AnyServerConnected
    {
        get
        {
            return _syncHubClients.Any(client => client.Value.ServerState == ServerState.Connected);
        }
    }

    public bool AnyServerConnecting
    {
        get
        {
            return _syncHubClients.Any(client => client.Value.ServerState == ServerState.Connecting);
        }
    }

    public bool AnyServerDisconnecting
    {
        get
        {
            return _syncHubClients.Any(client => client.Value.ServerState == ServerState.Disconnecting);
        }
    }

    public int[] ConnectedServerIndexes {
        get
        {
            return [.._syncHubClients.Where(p=> p.Value.ServerState == ServerState.Connected)?.Select(p=> p.Key) ?? []];
        }
    }

    public bool IsServerConnecting(ServerIndex index)
    {
        return GetServerStateForServer(index) == ServerState.Connecting;
    }

    public int GetMaxGroupsJoinedByUser(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.ConnectionDto?.ServerInfo.MaxGroupsJoinedByUser ?? 0;
    }

    public int GetMaxGroupsCreatedByUser(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.ConnectionDto?.ServerInfo.MaxGroupsCreatedByUser ?? 0;
    }

    public string? GetAuthFailureMessageByServer(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.AuthFailureMessage;
    }

    public string GetUidByServer(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.UID ?? string.Empty;
    }

    public string GetDisplayNameByServer(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.ConnectionDto?.User.AliasOrUID ?? string.Empty;
    }

    public async Task PauseConnectionAsync(ServerIndex serverIndex)
    {
        _syncHubClients.TryRemove(serverIndex, out SyncHubClient? removed);
        if (removed != null)
        {
            await removed.DisposeConnectionAsync().ConfigureAwait(false);
        }
    }

    public async Task CreateConnectionsAsync(ServerIndex serverIndex)
    {
        await ConnectMultiClient(serverIndex).ConfigureAwait(false);
    }

    public void CyclePauseAsync(ServerIndex serverIndex, UserData userData)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var client = GetClientForServer(serverIndex);
        if (client is not null)
            TaskHelpers.FireAndForget(() => client.CyclePauseAsync(serverIndex, userData), Logger, cts.Token);
    }

    private SyncHubClient CreateNewClient(ServerIndex serverIndex)
    {
        return new SyncHubClient(serverIndex, _serverConfigManager, _pairManager, _dalamudUtil,
            _loggerFactory, _loggerProvider, Mediator, _multiConnectTokenService, _syncConfigService, _httpClient);
    }

    private SyncHubClient? GetClientForServer(ServerIndex serverIndex)
    {
        _syncHubClients.TryGetValue(serverIndex, out var client);
        return client;

    }

    private SyncHubClient GetOrCreateForServer(ServerIndex serverIndex, [CallerMemberName] string callerName = "")
    {
        Logger.LogDebug("({CallerName}) GetOrCreateForServer: serverIndex={ServerIndex}", callerName, serverIndex);
        var client = _syncHubClients.GetOrAdd(serverIndex, CreateNewClient);
        return client;
    }

    private Task ConnectMultiClient(ServerIndex serverIndex)
    {
        return GetOrCreateForServer(serverIndex).CreateConnectionsAsync();
    }

    public void AutoConnectClients()
    {
        if (!_serverConfigManager.ServerIndexes.Any())
            return;

        Mediator.Publish(new EventMessage(new Event(nameof(ApiController), EventSeverity.Informational,
            $"Auto-connecting clients initiated.")));

        using var cts = new CancellationTokenSource();
        TaskHelpers.FireAndForget(async () =>
        {
            var charaName = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(charaName))
                return; // No character name means the player isnt logged in, so no auto-connect

            var worldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);

            Logger.LogInformation("Auto-connecting clients for character {Character} on world {WorldId}", charaName, worldId);

            var tasks = new HashSet<Task>();

            foreach (int serverIndex in _serverConfigManager.ServerIndexes)
            {
                var server = _serverConfigManager.GetServerByIndex(serverIndex);

                if(server.Authentications.Any(p=> !p.AutoLogin && p.CharacterName.Equals(charaName, StringComparison.OrdinalIgnoreCase) && p.WorldId == worldId))
                {
                    Logger.LogDebug("Skipping auto-connect for {Server} because auto-login is disabled for {Character}", server.ServerName, charaName);
                    continue;
                }

                // When you manually disconnect a service it gets full paused. In that case, the user explicitly asked for it
                // not to be connected, so we'll just leave it
                // Manually connecting once triggers auto connects again!
                if (!server.FullPause)
                {
                    tasks.Add(CreateConnectionForServer(serverIndex));
                }
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }, Logger, cts.Token);
    }

    private Task CreateConnectionForServer(ServerIndex serverIndex)
    {
        return GetOrCreateForServer(serverIndex).CreateConnectionsAsync();
    }

    protected override void Dispose(bool disposing)
    {
        DisposeAllClients();
        base.Dispose(disposing);
    }

    private void DisposeAllClients()
    {
        // We can always just Dispose() this - even if the client is currently not connected. Getting rid of them all
        // this way is the safest way to prevent connection leaks. If we'd wait for each of them to disconnect first,
        // we might run into FF14 exiting or similar before they are connected (if you really have to have a lot of connections)
        foreach (var syncHubClient in _syncHubClients.Values)
        {
            DisposeAllClientsAsync().GetAwaiter().GetResult();
            _syncHubClients.Clear();
        }
    }

    private async Task DisposeAllClientsAsync()
    {
        var disposeTasks = _syncHubClients.Values
            .Select(client => client.DisposeConnectionAsync());
        await Task.WhenAll(disposeTasks).ConfigureAwait(false);
    }
}
#pragma warning restore MA0040