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

        AutoConnectClients();
    }

    public ServerState GetServerState(ServerIndex index)
    {
        return GetClientForServer(index)?.ServerState ?? ServerState.Offline;
    }

    public bool IsServerConnected(ServerIndex index)
    {
        return GetClientForServer(index)?.ServerState == ServerState.Connected;
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
        var serverState = GetServerState(index);
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

    public ServerState GetServerStateForServer(ServerIndex index)
    {
        // No client found means it's offline
        return GetClientForServer(index)?.ServerState ?? ServerState.Offline;
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
        Mediator.Publish(new EventMessage(new Event(nameof(ApiController), EventSeverity.Informational,
            $"Auto-connecting clients initiated.")));

        using var cts = new CancellationTokenSource();
        TaskHelpers.FireAndForget(async () =>
        {
            var charaName = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
            var worldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);

            Logger.LogInformation("Auto-connecting clients for character {Character} on world {WorldId}", charaName, worldId);

            foreach (int serverIndex in _serverConfigManager.ServerIndexes)
            {
                var server = _serverConfigManager.GetServerByIndex(serverIndex);
                if (!server.FullPause)
                {
                    await GetOrCreateForServer(serverIndex).DalamudUtilOnLogIn(charaName, worldId).ConfigureAwait(false);
                }
            }
        }, Logger, cts.Token);
    }

    protected override void Dispose(bool disposing)
    {
        if(disposing)
        {
            DisposeAllClientsAsync().GetAwaiter().GetResult();
            _syncHubClients.Clear();
        }

        base.Dispose(disposing);
    }

    private async Task DisposeAllClientsAsync()
    {
        var disposeTasks = _syncHubClients.Values
            .Select(client => client.DisposeConnectionAsync());
        await Task.WhenAll(disposeTasks).ConfigureAwait(false);
    }
}
#pragma warning restore MA0040