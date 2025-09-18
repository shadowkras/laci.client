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

    /// <summary>
    /// Gets the server state for the given server index.
    /// </summary>
    /// <returns></returns>
    public ServerState GetServerState(ServerIndex index)
    {
        return GetClientForServer(index)?.ServerState ?? ServerState.Offline;
    }

    /// <summary>
    /// Returns true if the server at the given index is connected.
    /// </summary>
    /// <returns></returns>
    public bool IsServerConnected(ServerIndex index)
    {
        return GetClientForServer(index)?.ServerState == ServerState.Connected;
    }

    /// <summary>
    /// Gets the server name for the given server index.
    /// </summary>
    /// <returns></returns>
    public string GetServerNameByIndex(ServerIndex index)
    {
        return _serverConfigManager.GetServerByIndex(index).ServerName ?? string.Empty;
    }

    /// <summary>
    /// Gets the number of online users for the given server index.
    /// </summary>
    /// <returns></returns>
    public int GetOnlineUsersForServer(ServerIndex index)
    {
        return GetClientForServer(index)?.SystemInfoDto?.OnlineUsers ?? 0;
    }

    /// <summary>
    /// Returns true if the server is in a state that it can be considered "alive", meaning it's not offline or in an error state.
    /// </summary>
    /// <returns></returns>
    public bool IsServerAlive(int index)
    {
        var serverState = GetServerState(index);
        return serverState is ServerState.Connected or ServerState.RateLimited
            or ServerState.Unauthorized or ServerState.Disconnected;
    }

    /// <summary>
    /// Gets the total number of online users across all connected servers.
    /// </summary>
    public int OnlineUsers
    {
        get
        {
            return _syncHubClients.Sum(entry => entry.Value.SystemInfoDto?.OnlineUsers ?? 0);
        }
    }

    /// <summary>
    /// Gets the server info for the given server index.
    /// </summary>
    /// <returns></returns>
    public ServerInfo? GetServerInfoForServer(ServerIndex index)
    {
        return GetClientForServer(index)?.ConnectionDto?.ServerInfo;
    }

    /// <summary>
    /// Gets the default permissions for the given server index.
    /// </summary>
    /// <returns></returns>
    public DefaultPermissionsDto? GetDefaultPermissionsForServer(ServerIndex index)
    {
        return GetClientForServer(index)?.ConnectionDto?.DefaultPreferredPermissions;
    }

    /// <summary>
    /// Gets the server state for the given server index.
    /// </summary>
    /// <returns></returns>
    public ServerState GetServerStateForServer(ServerIndex index)
    {
        // No client found means it's offline
        return GetClientForServer(index)?.ServerState ?? ServerState.Offline;
    }

    /// <summary>
    /// Returns true if any server is currently connected.
    /// </summary>
    public bool AnyServerConnected
    {
        get
        {
            return _syncHubClients.Any(client => client.Value.ServerState == ServerState.Connected);
        }
    }

    /// <summary>
    /// Returns true if any server is currently in the process of connecting.
    /// </summary>
    public bool AnyServerConnecting
    {
        get
        {
            return _syncHubClients.Any(client => client.Value.ServerState == ServerState.Connecting);
        }
    }

    /// <summary>
    /// Returns true if any server is currently in the process of disconnecting.
    /// </summary>
    public bool AnyServerDisconnecting
    {
        get
        {
            return _syncHubClients.Any(client => client.Value.ServerState == ServerState.Disconnecting);
        }
    }

    /// <summary>
    /// Gets the indexes of all currently connected servers.
    /// </summary>
    public int[] ConnectedServerIndexes {
        get
        {
            return [.._syncHubClients.Where(p=> p.Value.ServerState == ServerState.Connected)?.Select(p=> p.Key) ?? []];
        }
    }

    /// <summary>
    /// Returns true if the server at the given index is in the process of connecting state.
    /// </summary>
    /// <returns></returns>
    public bool IsServerConnecting(ServerIndex index)
    {
        return GetServerStateForServer(index) == ServerState.Connecting;
    }

    /// <summary>
    /// Gets the maximum number of syncshells a user can join on the given server.
    /// </summary>
    /// <returns></returns>
    public int GetMaxGroupsJoinedByUser(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.ConnectionDto?.ServerInfo.MaxGroupsJoinedByUser ?? 0;
    }

    /// <summary>
    /// Gets the maximum number of syncshells a user can create on the given server.
    /// </summary>
    /// <param name="serverIndex"></param>
    /// <returns></returns>
    public int GetMaxGroupsCreatedByUser(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.ConnectionDto?.ServerInfo.MaxGroupsCreatedByUser ?? 0;
    }

    /// <summary>
    /// Gets the authentication failure message for the given server index, if any.
    /// </summary>
    /// <returns></returns>
    public string? GetAuthFailureMessageByServer(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.AuthFailureMessage;
    }

    /// <summary>
    /// Gets the UID of the connected user for the given server index, or an empty string if not connected.
    /// </summary>
    /// <returns></returns>
    public string GetUidByServer(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.UID ?? string.Empty;
    }

    /// <summary>
    /// Gets the display name (alias or UID) of the connected user for the given server index, or an empty string if not connected.
    /// </summary>
    /// <returns></returns>
    public string GetDisplayNameByServer(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.ConnectionDto?.User.AliasOrUID ?? string.Empty;
    }

    /// <summary>
    /// Pauses the connection to the server at the given index, disposing of the client.
    /// </summary>
    /// <returns></returns>
    public async Task PauseConnectionAsync(ServerIndex serverIndex)
    {
        _syncHubClients.TryRemove(serverIndex, out SyncHubClient? removed);
        if (removed != null)
        {
            await removed.DisposeConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates connections for the server at the given index, if not already connected.
    /// </summary>
    /// <returns></returns>
    public async Task CreateConnectionsAsync(ServerIndex serverIndex)
    {
        await ConnectMultiClient(serverIndex).ConfigureAwait(false);
    }

    /// <summary>
    /// Cycles the pause state of the connection for the server at the given index.
    /// </summary>
    public void CyclePauseAsync(ServerIndex serverIndex, UserData userData)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var client = GetClientForServer(serverIndex);
        if (client is not null)
            TaskHelpers.FireAndForget(() => client.CyclePauseAsync(serverIndex, userData), Logger, cts.Token);
    }

    /// <summary>
    /// Creates a new SyncHubClient for the given server index.
    /// </summary>
    /// <returns></returns>
    private SyncHubClient CreateNewClient(ServerIndex serverIndex)
    {
        return new SyncHubClient(serverIndex, _serverConfigManager, _pairManager, _dalamudUtil,
            _loggerFactory, _loggerProvider, Mediator, _multiConnectTokenService, _syncConfigService, _httpClient);
    }

    /// <summary>
    /// Gets the SyncHubClient for the given server index, or null if not found.
    /// </summary>
    /// <returns></returns>
    private SyncHubClient? GetClientForServer(ServerIndex serverIndex)
    {
        _syncHubClients.TryGetValue(serverIndex, out var client);
        return client;

    }

    /// <summary>
    /// Gets or creates the SyncHubClient for the given server index.
    /// </summary>
    /// <returns></returns>
    private SyncHubClient GetOrCreateForServer(ServerIndex serverIndex, [CallerMemberName] string callerName = "")
    {
        Logger.LogDebug("({CallerName}) GetOrCreateForServer: serverIndex={ServerIndex}", callerName, serverIndex);
        var client = _syncHubClients.GetOrAdd(serverIndex, CreateNewClient);
        return client;
    }

    /// <summary>
    /// Connects the SyncHubClient for the given server index, creating it if necessary.
    /// </summary>
    /// <returns></returns>
    private Task ConnectMultiClient(ServerIndex serverIndex)
    {
        return GetOrCreateForServer(serverIndex).CreateConnectionsAsync();
    }

    /// <summary>
    /// Automatically connects clients for all servers that are not fully paused.
    /// </summary>
    public void AutoConnectClients()
    {
        Mediator.Publish(new EventMessage(new Event(nameof(ApiController), EventSeverity.Informational,
            $"Auto-connecting clients initiated.")));

        // Fire and forget the auto connect. if something goes wrong, it'll be displayed in UI
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

    /// <summary>
    /// Disposes all clients and their connections when the ApiController is disposed.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if(disposing)
        {
            _ = DisposeAllClientsAsync();
            _syncHubClients.Clear();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Disposes all connections on all sync clients registered.
    /// </summary>
    /// <returns></returns>
    private async Task DisposeAllClientsAsync()
    {
        var disposeTasks = _syncHubClients.Values
            .Select(client => client.DisposeConnectionAsync());
        await Task.WhenAll(disposeTasks).ConfigureAwait(false);
    }
}
#pragma warning restore MA0040