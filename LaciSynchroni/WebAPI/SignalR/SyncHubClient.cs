using Dalamud.Utility;
using LaciSynchroni.Common.Data;
using LaciSynchroni.Common.Data.Extensions;
using LaciSynchroni.Common.Dto;
using LaciSynchroni.Common.Dto.User;
using LaciSynchroni.Common.SignalR;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Events;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration;
using LaciSynchroni.SyncConfiguration.Models;
using LaciSynchroni.Utils;
using LaciSynchroni.WebAPI.SignalR;
using LaciSynchroni.WebAPI.SignalR.Utils;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;

namespace LaciSynchroni.WebAPI;

public partial class SyncHubClient : DisposableMediatorSubscriberBase, IServerHubClient
{
    /// <summary>
    /// Index of the server we are currently connected to, <see cref="LaciSynchroni.SyncConfiguration.Models.ServerStorage"/>
    /// </summary>
    public readonly int ServerIndex;

    private readonly bool _isWine;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly ILoggerProvider _loggerProvider;
    private readonly MultiConnectTokenService _multiConnectTokenService;
    private readonly PairManager _pairManager;
    private readonly SyncConfigService _syncConfigService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ServerConfigurationManager _serverConfigurationManager;

    private readonly Random _random = new Random();
    private const int _baseReconnectSeconds = 5;
    private const int _maxReconnectBackoff = 60;
    private const double _jitterReconnectFactor = 0.2;

    /// <summary>
    /// SignalR hub connection, one is maintained per server
    /// </summary>
    private HubConnection? _connection;
    private bool _isDisposed = false;
    private bool _initialized;
    private bool _naggedAboutLod = false;

    public ServerState ServerState { get; private set; }
    private CancellationTokenSource? _healthCheckTokenSource = new();
    private CancellationTokenSource? _connectionCancellationTokenSource;
    private bool _doNotNotifyOnNextInfo = false;

    public string AuthFailureMessage { get; private set; } = string.Empty;
    private string? _lastUsedToken;
    private CensusUpdateMessage? _lastCensus;

    public ConnectionDto? ConnectionDto { get; private set; }

    public SystemInfoDto? SystemInfoDto { get; private set; }

    public string UID => ConnectionDto?.User?.UID ?? string.Empty;

    protected bool IsConnected => ServerState == ServerState.Connected;

    private ServerStorage ServerToUse => _serverConfigurationManager.GetServerByIndex(ServerIndex);

    private string ServerName => ServerToUse?.ServerName ?? "Unknown service";

    public SyncHubClient(int serverIndex,
        ServerConfigurationManager serverConfigurationManager, PairManager pairManager,
        DalamudUtilService dalamudUtilService,
        ILoggerFactory loggerFactory, ILoggerProvider loggerProvider, SyncMediator mediator, MultiConnectTokenService multiConnectTokenService, SyncConfigService syncConfigService, HttpClient httpClient) :
        base(loggerFactory.CreateLogger($"{nameof(SyncHubClient)}Mediator-{serverIndex}"), mediator)
    {
        ServerIndex = serverIndex;
        _isWine = dalamudUtilService.IsWine;
        _loggerProvider = loggerProvider;
        _multiConnectTokenService = multiConnectTokenService;
        _syncConfigService = syncConfigService;
        _httpClient = httpClient;
        _pairManager = pairManager;
        _dalamudUtil = dalamudUtilService;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = loggerFactory.CreateLogger($"{nameof(SyncHubClient)}-{ServerName}");

        Mediator.Subscribe<CyclePauseMessage>(this, (msg) =>
        {
            Logger.LogTrace("Received CyclePauseMessage for server {ServerIndex}, this is {ThisServerIndex}", msg.ServerIndex, serverIndex);
            if (serverIndex == msg.ServerIndex)
                _ = CyclePauseAsync(msg.ServerIndex, msg.UserData);
        });
        Mediator.Subscribe<CensusUpdateMessage>(this, (msg) => _lastCensus = msg);
        Mediator.Subscribe<PauseMessage>(this, (msg) =>
        {
            if (serverIndex == msg.ServerIndex)
                _ = PauseAsync(msg.ServerIndex, msg.UserData);
        });
        Mediator.Subscribe<UserAddPairMessage>(this, (msg) =>
        {
            if (serverIndex == msg.ServerIndex)
            {
                var sendPairNotification = _serverConfigurationManager.GetServerByIndex(ServerIndex)?.ShowPairingRequestNotification ?? false;
                _ = UserAddPair(new UserDto(msg.UserData), sendPairNotification);

                if(sendPairNotification)
                    _ = TryPairWithContentId(msg.UserData.UID); //Pair request confirmation compatibility.
            }
        });
    }

    public async Task CreateConnectionsAsync()
    {
        if (ServerToUse is null)
        {
            return;
        }

        if (!await VerifyCensus().ConfigureAwait(false))
        {
            return;
        }

        // We actually do the "full pause" handling in here
        // This is a candidate for refactoring in the future
        // Full pause = press the disconnect button
        if (!await VerifyFullPause().ConfigureAwait(false))
        {
            return;
        }

        // Since CreateConnectionAsync is a bit of a catch all, we go through a few validations first, see if we need to show any popups
        if (!await VerifySecretKeyAuth().ConfigureAwait(false))
        {
            return;
        }

        if (!await VerifyOAuth().ConfigureAwait(false))
        {
            return;
        }

        if(IsConnected)
        {
            // Just make sure no open connection exists. It shouldn't, but can't hurt (At least I assume that was the intent...)
            // Also set to "Connecting" at this point
            Logger.LogDebug("Already connected to {ServerName}, stopping connection", ServerName);
            await StopConnectionAsync(ServerState.Connecting).ConfigureAwait(false);
        }

        var serverHubUri = await FindServerHubUrl().ConfigureAwait(false);
        if (string.IsNullOrEmpty(serverHubUri?.AbsoluteUri))
        {
            await StopConnectionAsync(ServerState.NoHubFound).ConfigureAwait(false);
            return;
        }

        Logger.LogInformation("Recreating Connection to {ServerName}", ServerName);
        Mediator.Publish(new EventMessage(new Event(nameof(SyncHubClient), EventSeverity.Informational,
            $"Starting Connection to {ServerName}")));

        // If we have an old token, we clear it out so old tokens won't accidentally cancel the fresh connect attempt
        // This will also cancel out all ongoing connection attempts for this server, if any are still pending
        var connectionCancellationToken = CreateConnectToken();

        // Loop and try to establish connections
        await LoopConnections(serverHubUri, connectionCancellationToken).ConfigureAwait(false);
    }

    private async Task LoopConnections(Uri serverHubUri, CancellationToken connectionCancellationToken)
    {
        int backoffSeconds = _baseReconnectSeconds;

        while (ServerToUse is not null &&
               ServerState is not ServerState.Connected &&
               !connectionCancellationToken.IsCancellationRequested)
        {
            try
            {
                if (connectionCancellationToken.IsCancellationRequested)
                    return;

                await TryConnect(serverHubUri, connectionCancellationToken).ConfigureAwait(false);
                AuthFailureMessage = string.Empty;
                backoffSeconds = _baseReconnectSeconds;
                return;
            }
            catch (OperationCanceledException)
            {
                // cancellation token was triggered, either through user input or an error
                // if this is the case, we just log it and leave. Connection wasn't established set, nothing to erase
                _logger.LogWarning("Connection to {ServerName} attempt cancelled", ServerName);
                return;
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogWarning(ex, "InvalidOperationException on connection to {ServerName}", ServerName);
                await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException ex)
            {
                var statusCode = ex.StatusCode ?? HttpStatusCode.ServiceUnavailable;
                _logger.LogWarning(ex, "HttpRequestException on connection to {ServerName} ({ServerHubUri}) (StatusCode {StatusCode}) ",
                    ServerName, serverHubUri, statusCode);

                if (ex.StatusCode is HttpStatusCode.Unauthorized)
                {
                    // we don't want to spam our auth server. Raze down the connection and leave
                    await StopConnectionAsync(ServerState.Unauthorized).ConfigureAwait(false);
                    return;
                }

                ServerState = ServerState.Reconnecting;
                backoffSeconds = await HandleReconnectDelay(ex.Message, backoffSeconds, connectionCancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Exception on connection to {ServerName} ({ServerHubUri})", ServerName, ServerToUse.ServerHubUri);
                backoffSeconds = await HandleReconnectDelay(LogLevel.Critical, ex.Message, backoffSeconds, connectionCancellationToken).ConfigureAwait(false);                
            }
        }

        if (connectionCancellationToken.IsCancellationRequested)
            Logger.LogInformation("Reconnect loop to {ServerName} exited due to cancellation token.", ServerName);
    }

    private async Task<int> HandleReconnectDelay(string reason, int backoffSeconds, CancellationToken cancelToken)
    {
        return await HandleReconnectDelay(LogLevel.Information, reason, backoffSeconds, cancelToken).ConfigureAwait(false);
    }

    private async Task<int> HandleReconnectDelay(LogLevel loglevel, string reason, int backoffSeconds, CancellationToken cancelToken)
    {
        double delay = GetRandomReconnectDelay(backoffSeconds);
        Logger.Log(loglevel, "Connection to {ServerName} failed ({Reason}), retrying in {Delay} seconds", ServerName, reason, (int)delay);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), cancelToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // cancellation by token, exit silently
            return backoffSeconds;
        }

        return GetIncreasedReconnectBackoff(backoffSeconds);
    }

    private double GetRandomReconnectDelay(int backoffSeconds)
    {
        double jitter = (_random.NextDouble() * 2 - 1) * _jitterReconnectFactor;
        double delay = backoffSeconds * (1.0 + jitter);
        return Math.Min(delay, _maxReconnectBackoff);
    }

    private static int GetIncreasedReconnectBackoff(int backoffSeconds)
    {
        return Math.Min(backoffSeconds * 2, _maxReconnectBackoff);
    }

    private CancellationToken CreateConnectToken()
    {
        CancelConnectToken();
        _connectionCancellationTokenSource?.Dispose();
        _connectionCancellationTokenSource = new CancellationTokenSource();
        var cancelReconnectToken = _connectionCancellationTokenSource.Token;
        return cancelReconnectToken;
    }

    private void CancelConnectToken()
    {
        if (!_connectionCancellationTokenSource?.IsCancellationRequested ?? false)
            _connectionCancellationTokenSource?.Cancel();
    }

    private async Task TryConnect(Uri serverHubUri, CancellationToken cancellationToken)
    {
        AuthFailureMessage = string.Empty;
        await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
        ServerState = ServerState.Connecting;

        await UpdateToken(cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _connection = InitializeHubConnection(serverHubUri, cancellationToken);
        InitializeApiHooks();

        await _connection.StartAsync(cancellationToken).ConfigureAwait(false);

        ConnectionDto = await GetConnectionDto().ConfigureAwait(false);
        if (!await VerifyClientVersion(ConnectionDto).ConfigureAwait(false))
        {
            await StopConnectionAsync(ServerState.VersionMisMatch).ConfigureAwait(false);
           Logger.LogWarning("Client version mismatch with service {ServerName}: {ServerVersion}", ServerName, ConnectionDto.ServerVersion);
        }
        // Also trigger some warnings
        TriggerConnectionWarnings(ConnectionDto);

        ServerState = ServerState.Connected;
        _logger.LogInformation("Connection to {ServerName} successful.", ServerName);

        await LoadInitialPairsAsync().ConfigureAwait(false);
        await LoadOnlinePairsAsync().ConfigureAwait(false);
    }

    private async Task<Uri?> FindServerHubUrl()
    {
        ServerState = ServerState.Connecting;

        var hubsToCheck = new List<Uri>();

        // Add advanced hub uri if available
        if (!string.IsNullOrEmpty(ServerToUse.ServerHubUri))
        {
            var configuredHubUri = new UriBuilder(ServerToUse.ServerHubUri).WsToHttp().Uri;
            hubsToCheck.Add(configuredHubUri);
        }

        var baseServerUri = new UriBuilder(ServerToUse.ServerUri).WsToHttp().Uri;

        hubsToCheck.Add(new Uri(baseServerUri, IServerHub.Path)); // Default hub path
        hubsToCheck.Add(new Uri(baseServerUri, "/mare")); // falloff hub path for compatibility

        foreach (var hubToCheck in hubsToCheck.Distinct())
        {
            if (string.IsNullOrEmpty(hubToCheck.AbsoluteUri))
                continue;

            var responseHub = await _httpClient.GetAsync(hubToCheck).ConfigureAwait(false);

            // If we get a 401, 200 or 204 we have found a hub. The last two should not happen, 401 means that the URL is valid and likely a hub connection
            // We could try to emulate the /negotiate, but for now, this should work just as well
            if (responseHub.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.OK or HttpStatusCode.NoContent)
            {
                Logger.LogInformation("{HubUri}: Valid hub address found for {ServerName}", hubToCheck, ServerName);
                return hubToCheck;
            }

            Logger.LogWarning("{HubUri}: Not valid, attempting next hub address for {ServerName}: ({StatusCode}) {StatusCodeText}", 
                hubToCheck, ServerName, responseHub.StatusCode, responseHub.StatusCode.ToString());
        }

        Logger.LogWarning("Unable to find any hub to connect to {ServerName}, aborting connection attempt.", ServerName);
        return null;
    }

    private async Task UpdateToken(CancellationToken cancellationToken)
    {
        try
        {
            _lastUsedToken = await _multiConnectTokenService.GetOrUpdateToken(ServerIndex, cancellationToken).ConfigureAwait(false);
        }
        catch (SyncAuthFailureException ex)
        {
            AuthFailureMessage = ex.Reason;
            throw new HttpRequestException($"Error during authentication to {ServerName}", ex, HttpStatusCode.Unauthorized);
        }
    }

    private async Task StopConnectionAsync(ServerState state)
    {
        ServerState = ServerState.Disconnecting;

        _logger.LogInformation("Stopping existing connection to {ServerName}", ServerName);

        if (_connection != null && !_isDisposed)
        {
            _logger.LogDebug("Disposing current HubConnection to {ServerName}", ServerName);

            _connection.Closed -= ConnectionOnClosed;
            _connection.Reconnecting -= ConnectionOnReconnecting;
            _connection.Reconnected -= ConnectionOnReconnectedAsync;

            await _connection.StopAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);

            _isDisposed = true;

            Mediator.Publish(new EventMessage(new Event(nameof(SyncHubClient), EventSeverity.Informational,
                $"Stopping existing connection to {ServerName}")));
            _initialized = false;

            if(!_healthCheckTokenSource?.IsCancellationRequested ?? false)
                _healthCheckTokenSource?.CancelAsync();

            Mediator.Publish(new DisconnectedMessage(ServerIndex));
            _connection = null;
            ConnectionDto = null;

            Logger.LogDebug("HubConnection to {ServerName} disposed", ServerName);
        }

        ServerState = state;
    }

    private HubConnection InitializeHubConnection(Uri serverHubUri, CancellationToken ct)
    {
        var transportType = ServerToUse.HttpTransportType switch
        {
            HttpTransportType.None => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents |
                                      HttpTransportType.LongPolling,
            HttpTransportType.WebSockets => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents |
                                            HttpTransportType.LongPolling,
            HttpTransportType.ServerSentEvents => HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
            HttpTransportType.LongPolling => HttpTransportType.LongPolling,
            _ => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling
        };

        if (_isWine && !ServerToUse.ForceWebSockets
                    && transportType.HasFlag(HttpTransportType.WebSockets))
        {
            _logger.LogDebug("Wine detected, falling back to ServerSentEvents / LongPolling");
            transportType = HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling;
        }

        _logger.LogDebug("Building new HubConnection to {ServerName} ({ServerHubUrl}) using transport {Transport}", ServerName, serverHubUri, transportType);

        _connection = new HubConnectionBuilder()
            .WithUrl(serverHubUri, options =>
            {
                options.AccessTokenProvider = () => _multiConnectTokenService.GetOrUpdateToken(ServerIndex, ct);
                options.Transports = transportType;
            })
            .AddMessagePackProtocol(opt =>
            {
                var resolver = CompositeResolver.Create(StandardResolverAllowPrivate.Instance,
                    BuiltinResolver.Instance,
                    AttributeFormatterResolver.Instance,
                    // replace enum resolver
                    DynamicEnumAsStringResolver.Instance,
                    DynamicGenericResolver.Instance,
                    DynamicUnionResolver.Instance,
                    DynamicObjectResolver.Instance,
                    PrimitiveObjectResolver.Instance,
                    // final fallback(last priority)www
                    StandardResolver.Instance);

                opt.SerializerOptions =
                    MessagePackSerializerOptions.Standard
                        .WithCompression(MessagePackCompression.Lz4Block)
                        .WithResolver(resolver);
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy(Mediator, ServerIndex))
            .ConfigureLogging(a =>
            {
                a.ClearProviders().AddProvider(_loggerProvider);
                a.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        _connection.Closed += ConnectionOnClosed;
        _connection.Reconnecting += ConnectionOnReconnecting;
        _connection.Reconnected += ConnectionOnReconnectedAsync;

        _isDisposed = false;
        return _connection;
    }

    private Task ConnectionOnClosed(Exception? arg)
    {
        CancelHealthCheckToken();

        Mediator.Publish(new DisconnectedMessage(ServerIndex));
        ServerState = ServerState.Offline;
        if (arg != null)
        {
            _logger.LogWarning(arg, "Connection to {ServerName} closed", ServerName);
        }
        else
        {
            _logger.LogInformation("Connection to {ServerName} closed", ServerName);
        }

        return Task.CompletedTask;
    }

    private async Task ConnectionOnReconnectedAsync(string? arg)
    {
        ServerState = ServerState.Reconnecting;
        try
        {
            InitializeApiHooks();
            ConnectionDto = await GetConnectionDtoAsync(publishConnected: false).ConfigureAwait(false);
            if (ConnectionDto.ServerVersion != IServerHub.ApiVersion && !ServerToUse.BypassVersionCheck)
            {
                await StopConnectionAsync(ServerState.VersionMisMatch).ConfigureAwait(false);
                return;
            }

            ServerState = ServerState.Connected;
            await LoadInitialPairsAsync().ConfigureAwait(false);
            await LoadOnlinePairsAsync().ConfigureAwait(false);
            Mediator.Publish(new ConnectedMessage(ConnectionDto, ServerIndex));
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Failure to obtain data from {ServerName} after reconnection", ServerName);
            await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
        }
    }

    private Task ConnectionOnReconnecting(Exception? arg)
    {
        _doNotNotifyOnNextInfo = true;
        CancelHealthCheckToken();
        ServerState = ServerState.Reconnecting;
        Logger.LogWarning(arg, "Connection to {ServerName} closed... Reconnecting", ServerName);
        Mediator.Publish(new EventMessage(new Event(nameof(SyncHubClient), EventSeverity.Warning,
            $"Connection interrupted, reconnecting to {ServerName}")));
        return Task.CompletedTask;
    }

    public Task<ConnectionDto> GetConnectionDto() => GetConnectionDtoAsync(true);

    public async Task<ConnectionDto> GetConnectionDtoAsync(bool publishConnected)
    {
        if (_connection is null)
            throw new InvalidOperationException($"Not connected to server {ServerName}");

        var dto = await _connection!.InvokeAsync<ConnectionDto>(nameof(GetConnectionDto)).ConfigureAwait(false);
        if (publishConnected)
            Mediator.Publish(new ConnectedMessage(dto, ServerIndex));
        return dto;
    }

    private void InitializeApiHooks()
    {
        if (_connection == null) return;

        Logger.LogDebug("Initializing data");
        OnDownloadReady((guid) => _ = Client_DownloadReady(guid));
        OnReceiveServerMessage((sev, msg) => _ = Client_ReceiveServerMessage(sev, msg));
        OnReceivePairingMessage((dto) => _ = Client_ReceivePairingMessage(dto));
        OnReceiveBroadcastPairRequest((dto) => _ = Client_ReceiveBroadcastPairRequest(dto));
        OnUpdateSystemInfo((dto) => _ = Client_UpdateSystemInfo(dto));

        OnUserSendOffline((dto) => _ = Client_UserSendOffline(dto));
        OnUserAddClientPair((dto) => _ = Client_UserAddClientPair(dto));
        OnUserReceiveCharacterData((dto) => _ = Client_UserReceiveCharacterData(dto));
        OnUserRemoveClientPair(dto => _ = Client_UserRemoveClientPair(dto));
        OnUserSendOnline(dto => _ = Client_UserSendOnline(dto));
        OnUserUpdateOtherPairPermissions(dto => _ = Client_UserUpdateOtherPairPermissions(dto));
        OnUserUpdateSelfPairPermissions(dto => _ = Client_UserUpdateSelfPairPermissions(dto));
        OnUserReceiveUploadStatus(dto => _ = Client_UserReceiveUploadStatus(dto));
        OnUserUpdateProfile(dto => _ = Client_UserUpdateProfile(dto));
        OnUserDefaultPermissionUpdate(dto => _ = Client_UserUpdateDefaultPermissions(dto));
        OnUpdateUserIndividualPairStatusDto(dto => _ = Client_UpdateUserIndividualPairStatusDto(dto));

        OnGroupChangePermissions((dto) => _ = Client_GroupChangePermissions(dto));
        OnGroupDelete((dto) => _ = Client_GroupDelete(dto));
        OnGroupPairChangeUserInfo((dto) => _ = Client_GroupPairChangeUserInfo(dto));
        OnGroupPairJoined((dto) => _ = Client_GroupPairJoined(dto));
        OnGroupPairLeft((dto) => _ = Client_GroupPairLeft(dto));
        OnGroupSendFullInfo((dto) => _ = Client_GroupSendFullInfo(dto));
        OnGroupSendInfo((dto) => _ = Client_GroupSendInfo(dto));
        OnGroupChangeUserPairPermissions((dto) => _ = Client_GroupChangeUserPairPermissions(dto));

        OnGposeLobbyJoin((dto) => _ = Client_GposeLobbyJoin(dto));
        OnGposeLobbyLeave((dto) => _ = Client_GposeLobbyLeave(dto));
        OnGposeLobbyPushCharacterData((dto) => _ = Client_GposeLobbyPushCharacterData(dto));
        OnGposeLobbyPushPoseData((dto, data) => _ = Client_GposeLobbyPushPoseData(dto, data));
        OnGposeLobbyPushWorldData((dto, data) => _ = Client_GposeLobbyPushWorldData(dto, data));

        _ = ClientHealthCheckAsync();

        _initialized = true;
    }

    private async Task ClientHealthCheckAsync()
    {
        using var cts = CreateHealthCheckToken();

        while (!cts.IsCancellationRequested && _connection != null)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cts.Token).ConfigureAwait(false);
                Logger.LogDebug("Checking Client Health State to {ServerName}", ServerName);

                bool requireReconnect = await RefreshTokenAsync(cts.Token).ConfigureAwait(false);
                if (requireReconnect)
                    break;

                _ = await CheckClientHealth().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // cancellation by token, exit silently
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Unexpected exception in {nameof(ClientHealthCheckAsync)}");
            }
        }
    }

    private CancellationTokenSource CreateHealthCheckToken()
    {
        var cts = new CancellationTokenSource();
        try
        {
            CancelHealthCheckToken();
            _healthCheckTokenSource?.Dispose();
            _healthCheckTokenSource = cts;
        }
        catch (OperationCanceledException) 
        {
            //Ignore silently, we just wanted to cancel the old one
        }

        return cts;
    }

    private void CancelHealthCheckToken()
    {
        if (!_healthCheckTokenSource?.IsCancellationRequested ?? false)
            _healthCheckTokenSource?.Cancel();
    }

    private async Task<bool> RefreshTokenAsync(CancellationToken ct)
    {
        bool requireReconnect = false;
        try
        {
            var token = await _multiConnectTokenService.GetOrUpdateToken(ServerIndex, ct).ConfigureAwait(false);
            if (!string.Equals(token, _lastUsedToken, StringComparison.Ordinal))
            {
                Logger.LogDebug("Reconnecting to {ServerName} due to updated token", ServerName);

                _doNotNotifyOnNextInfo = true;
                await CreateConnectionsAsync().ConfigureAwait(false);
                requireReconnect = true;
            }
        }
        catch (SyncAuthFailureException ex)
        {
            AuthFailureMessage = ex.Reason;
            await StopConnectionAsync(ServerState.Unauthorized).ConfigureAwait(false);
            requireReconnect = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not refresh token from {ServerName}, forcing reconnect", ServerName);
            _doNotNotifyOnNextInfo = true;
            await CreateConnectionsAsync().ConfigureAwait(false);
            requireReconnect = true;
        }

        return requireReconnect;
    }

    private async Task LoadInitialPairsAsync()
    {
        var groupsTask = GroupsGetAll();
        var userPairsTask = UserGetPairedClients();
        await Task.WhenAll(groupsTask, userPairsTask).ConfigureAwait(false);
        
        foreach (var entry in groupsTask.Result)
        {
            Logger.LogDebug("Group: {Entry}", entry);
            _pairManager.AddGroup(entry, ServerIndex);
        }

        foreach (var userPair in userPairsTask.Result)
        {
            Logger.LogDebug("Individual Pair: {UserPair}", userPair);
            _pairManager.AddUserPair(userPair, ServerIndex);
        }
    }

    private async Task LoadOnlinePairsAsync()
    {
        CensusDataDto? dto = null;
        if (_serverConfigurationManager.SendCensusData && _lastCensus != null)
        {
            var world = await _dalamudUtil.GetWorldIdAsync().ConfigureAwait(false);
            dto = new((ushort)world, _lastCensus.RaceId, _lastCensus.TribeId, _lastCensus.Gender);
            Logger.LogDebug("Attaching Census Data: {Dto}", dto);
        }

        foreach (var entry in await UserGetOnlinePairs(dto).ConfigureAwait(false))
        {
            Logger.LogDebug("Pair online: {Entry}", entry);
            _pairManager.MarkPairOnline(entry, ServerIndex, sendNotif: false);
        }
    }

    public string GetDisplayNameByContentId(string hashedCid)
    {
        if (string.IsNullOrWhiteSpace(hashedCid))
        {
            return string.Empty;
        }

        var (name, address) = _dalamudUtil.FindPlayerByNameHash(hashedCid);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var worldName = _dalamudUtil.GetWorldNameFromPlayerAddress(address);
            return !string.IsNullOrWhiteSpace(worldName)
                ? $"{name} @ {worldName}"
                : name;
        }

        var pair = _pairManager
            .GetOnlineUserPairs(ServerIndex)
            .FirstOrDefault(p => string.Equals(p.Ident, hashedCid, StringComparison.Ordinal));

        if (pair != null)
        {
            if (!string.IsNullOrWhiteSpace(pair.PlayerName))
            {
                return pair.PlayerName;
            }

            if (!string.IsNullOrWhiteSpace(pair.UserData.AliasOrUID))
            {
                return pair.UserData.AliasOrUID;
            }
        }

        return string.Empty;
    }

    public async Task DisposeConnectionAsync()
    {
        try
        {
            if (!_connectionCancellationTokenSource?.IsCancellationRequested ?? false)
                _connectionCancellationTokenSource?.CancelAsync();

            _connectionCancellationTokenSource?.Dispose();
            await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Silently ignore, we just wanted to cancel the token
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error stopping connection to {ServerName}.", ServerName);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(true);
        _ = Task.Run(async () => await DisposeConnectionAsync().ConfigureAwait(false));
    }

    public async Task<bool> CheckClientHealth()
    {
        if (_connection is null)
            return false;

        return await _connection.InvokeAsync<bool>(nameof(CheckClientHealth)).ConfigureAwait(false);
    }

    public async Task DalamudUtilOnLogIn(string characterName, uint worldId)
    {
        var auth = ServerToUse?.Authentications?
            .Find(f => string.Equals(f.CharacterName, characterName, StringComparison.Ordinal) && f.WorldId == worldId);
        if (auth?.AutoLogin ?? false)
        {
            Logger.LogInformation("Logging into {ServerName} as {CharaName}", ServerName, characterName);
            await CreateConnectionsAsync().ConfigureAwait(false);
        }
        else
        {
            Logger.LogInformation("Could not login into {ServerName} as {CharaName}, auto login is disabled", ServerName, characterName);
            await StopConnectionAsync(ServerState.NoAutoLogon).ConfigureAwait(false);
        }
    }

    public Task CyclePauseAsync(int serverIndex, UserData userData)
    {
        if (serverIndex != ServerIndex)
        {
            return Task.CompletedTask;
        }

        using var cts = new CancellationTokenSource();
        TaskHelpers.FireAndForget(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                var onlinePairs = _pairManager.GetOnlineUserPairs(ServerIndex);
                if (!onlinePairs.Any(p => p.UserPair != null && p.UserData.AliasOrUID.Equals(userData.AliasOrUID)))
                {
                    Logger.LogInformation("User {AliasOrUID} is not online on server {ServerName}, cannot cycle pause", userData.AliasOrUID, ServerName);
                    return;
                }

                var pair = onlinePairs.SingleOrDefault(p => p.UserPair != null && p.UserData == userData);
                if (pair is null)
                    return;

                var perm = pair.UserPair!.OwnPermissions;
                perm.SetPaused(paused: true);

                await UserSetPairPermissions(new UserPermissionsDto(userData, perm)).ConfigureAwait(false);

                // wait until it's changed
                while (pair.UserPair!.OwnPermissions != perm)
                {
                    await Task.Delay(250, cts.Token).ConfigureAwait(false);
                    Logger.LogTrace("Waiting for permissions change for {UserData}", userData);
                }

                perm.SetPaused(paused: false);
                await UserSetPairPermissions(new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
                Logger.LogInformation("Cycle pause state for User {AliasOrUID} on server {ServerName} was successful", userData.AliasOrUID, ServerName);
            }
            catch (OperationCanceledException)
            {
                Logger.LogWarning("CyclePauseAsync timed out for user {UserData} on {ServerName}", userData, ServerName);
            }
        }, Logger, cts.Token);

        return Task.CompletedTask;
    }

    private async Task PauseAsync(int serverIndex, UserData userData)
    {
        if (serverIndex != ServerIndex)
        {
            return;
        }
        var pair = _pairManager.GetOnlineUserPairs(ServerIndex).Single(p => p.UserPair != null && p.UserData == userData);
        var perm = pair.UserPair!.OwnPermissions;
        perm.SetPaused(paused: true);
        await UserSetPairPermissions(new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
    }
}