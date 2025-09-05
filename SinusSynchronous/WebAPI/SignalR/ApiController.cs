using Dalamud.Utility;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using SinusSynchronous.API.Data;
using SinusSynchronous.API.Data.Extensions;
using SinusSynchronous.API.Dto;
using SinusSynchronous.API.Dto.User;
using SinusSynchronous.API.SignalR;
using SinusSynchronous.PlayerData.Pairs;
using SinusSynchronous.Services;
using SinusSynchronous.Services.Mediator;
using SinusSynchronous.Services.ServerConfiguration;
using SinusSynchronous.SinusConfiguration;
using SinusSynchronous.SinusConfiguration.Models;
using SinusSynchronous.WebAPI.SignalR;
using SinusSynchronous.WebAPI.SignalR.Utils;
using System.Collections.Concurrent;
using System.Reflection;

namespace SinusSynchronous.WebAPI;
using ServerIndex = int;

#pragma warning disable MA0040
public sealed partial class ApiController : DisposableMediatorSubscriberBase
{
    public const string MainServer = "Sinus Synchronous";
    public const string MainServiceUri = "wss://sinus.syrilai.dev";

    private readonly DalamudUtilService _dalamudUtil;
    private readonly HubFactory _hubFactory;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly TokenProvider _tokenProvider;
    private readonly SinusConfigService _sinusConfigService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILoggerProvider _loggerProvider;
    private readonly HttpClient _httpClient;
    private readonly MultiConnectTokenService _multiConnectTokenService;
    private CancellationTokenSource _connectionCancellationTokenSource;
    private ConnectionDto? _connectionDto;
    private bool _doNotNotifyOnNextInfo = false;
    private CancellationTokenSource? _healthCheckTokenSource = new();
    private bool _initialized;
    private string? _lastUsedToken;
    private string? _authFailureMessage = string.Empty;
    private HubConnection? _sinusHub;
    private ServerState _serverState;
    private CensusUpdateMessage? _lastCensus;

    /// <summary>
    /// In preparaption for multi connect, all of the SignalR connection functionality has been moved into an
    /// instantiated, not injected, client.
    /// That client, internally, uses the server index to access the server object, and everything is based on that index. No more
    /// usage of ServerConfigurationManager#Current server
    /// Down the line, we move this into a list or a Map of active servers.
    /// </summary>
    private readonly ConcurrentDictionary<ServerIndex, MultiConnectSinusClient> _sinusClients = new();

    private bool _naggedAboutLod = false;

    public ApiController(ILogger<ApiController> logger, ILoggerFactory loggerFactory, HttpClient httpClient,
        HubFactory hubFactory, DalamudUtilService dalamudUtil, ILoggerProvider loggerProvider,
        PairManager pairManager, ServerConfigurationManager serverManager, SinusMediator mediator,
        TokenProvider tokenProvider, SinusConfigService sinusConfigService, MultiConnectTokenService multiConnectTokenService) : base(logger, mediator)
    {
        _hubFactory = hubFactory;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _serverManager = serverManager;
        _tokenProvider = tokenProvider;
        _sinusConfigService = sinusConfigService;
        _multiConnectTokenService = multiConnectTokenService;
        _httpClient = httpClient;
        _loggerFactory = loggerFactory;
        _loggerProvider = loggerProvider;
        _connectionCancellationTokenSource = new CancellationTokenSource();

        if (!UseMultiConnect)
        {
            Mediator.Subscribe<DalamudLoginMessage>(this, (_) => DalamudUtilOnLogIn());
            Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => DalamudUtilOnLogOut());
            Mediator.Subscribe<HubClosedMessage>(this, (msg) => SinusHubOnClosed(msg.Exception));
            Mediator.Subscribe<HubReconnectedMessage>(this, (msg) => _ = SinusHubOnReconnectedAsync());
            Mediator.Subscribe<HubReconnectingMessage>(this, (msg) => SinusHubOnReconnecting(msg.Exception));
            Mediator.Subscribe<CyclePauseMessage>(this, (msg) => _ = CyclePauseAsync(msg.ServerIndex, msg.UserData));
            Mediator.Subscribe<CensusUpdateMessage>(this, (msg) => _lastCensus = msg);
            Mediator.Subscribe<PauseMessage>(this, (msg) => _ = PauseAsync(msg.UserData));

            // Auto connect current server
            ServerState = ServerState.Offline;

            if (_dalamudUtil.IsLoggedIn)
            {
                DalamudUtilOnLogIn();
            }
        }
        else
        {
            // TODO
            // Auto connect every server. Ideally in sequence, in an extra method
            GetOrCreateForServer(_serverManager.CurrentServerIndex).DalamudUtilOnLogIn();
        }
    }

    private bool UseMultiConnect => _serverManager.EnableMultiConnect;

    public string AuthFailureMessage
    {
        get
        {
            if (this.UseMultiConnect)
            {
                return GetClientForServer(_serverManager.CurrentServerIndex)?.AuthFailureMessage ?? string.Empty;
            }

            return _authFailureMessage ?? string.Empty;
        }
        private set
        {
            _authFailureMessage = value;
        }
    }

    private ConnectionDto? CurrentConnectionDto
    {
        get
        {
            if (UseMultiConnect)
            {
                // For now, display for the one selected in drop-down. Later, we will have to do this per-server
                return GetClientForServer(_serverManager.CurrentServerIndex)?.ConnectionDto;
            }

            return _connectionDto;
        }
    }

    public DefaultPermissionsDto? DefaultPermissions => CurrentConnectionDto?.DefaultPreferredPermissions ?? null;

    public string DisplayName => CurrentConnectionDto?.User.AliasOrUID ?? string.Empty;

    public bool IsConnected => ServerState == ServerState.Connected;

    public bool IsServerConnected(int index)
    {
        return GetClientForServer(index)?._serverState == ServerState.Connected;
    }

    public int OnlineUsers
    {
        get
        {
            if (UseMultiConnect)
            {
                // For now, let's just sum each of them.
                return _sinusClients.Sum(entry => entry.Value.SystemInfoDto?.OnlineUsers ?? 0);
            }

            return SystemInfoDto.OnlineUsers;
        }
    }
    
    public int GetOnlineUsersForServer(ServerIndex index) {
        return GetClientForServer(index)?.SystemInfoDto?.OnlineUsers ?? 0;
    }

    public bool AnyServerConnected
    {
        get
        {
            return _sinusClients.Any(client => client.Value._serverState == ServerState.Connected);
        }
    }

    public bool ServerAlive => ServerState is ServerState.Connected or ServerState.RateLimited
        or ServerState.Unauthorized or ServerState.Disconnected;

    public ServerInfo ServerInfo => CurrentConnectionDto?.ServerInfo ?? new ServerInfo();

    public ServerState ServerState
    {
        get
        {
            if (UseMultiConnect)
            {
                // For now, display for the one selected in drop-down. Later, we will have to do this per-server
                return GetClientForServer(_serverManager.CurrentServerIndex)?._serverState ?? ServerState.Offline;
            }

            return _serverState;
        }
        private set
        {
            Logger.LogDebug("New ServerState: {value}, prev ServerState: {_serverState}", value, _serverState);
            _serverState = value;
        }
    }

    [Obsolete("Can be removed once we clean out old client code")]
    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public string UID => CurrentConnectionDto?.User.UID ?? string.Empty;

    public int[] ConnectedServerIndexes {
        get
        {
            if (UseMultiConnect)
            {
                return [.._sinusClients.Keys];
            }

            return [_serverManager.CurrentServerIndex];
        }
    }

    public int GetMaxGroupsJoinedByUser(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.ConnectionDto?.ServerInfo.MaxGroupsJoinedByUser ?? 0;
    }

    public string GetUidByServer(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.UID ?? string.Empty;
    }

    public async Task<bool> CheckClientHealth(ServerIndex serverIndex)
    {
        if (UseMultiConnect)
        {
            return await GetOrCreateForServer(serverIndex).CheckClientHealth().ConfigureAwait(false);
        }

        return await _sinusHub!.InvokeAsync<bool>(nameof(CheckClientHealth)).ConfigureAwait(false);
    }

    public async Task PauseConnectionAsync(ServerIndex serverIndex)
    {
        _sinusClients.TryRemove(serverIndex, out MultiConnectSinusClient? removed);
        if (removed != null)
        {
            await removed.DisposeAsync().ConfigureAwait(false);
        }

    }
    
    public async Task CreateConnectionsAsync(ServerIndex serverIndex)
    {
        if (UseMultiConnect)
        {
            await ConnectMultiClient(serverIndex).ConfigureAwait(false);
            return;
        }

        if (!_serverManager.ShownCensusPopup)
        {
            Mediator.Publish(new OpenCensusPopupMessage());
            while (!_serverManager.ShownCensusPopup)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }

        Logger.LogDebug("CreateConnections called");

        if (_serverManager.CurrentServer?.FullPause ?? true)
        {
            Logger.LogInformation("Not recreating Connection, paused");
            _connectionDto = null;
            await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
            _connectionCancellationTokenSource?.Cancel();
            return;
        }

        if (!_serverManager.CurrentServer.UseOAuth2)
        {
            var secretKey = _serverManager.GetSecretKey(out bool multi);
            if (multi)
            {
                Logger.LogWarning("Multiple secret keys for current character");
                _connectionDto = null;
                Mediator.Publish(new NotificationMessage("Multiple Identical Characters detected",
                    "Your Service configuration has multiple characters with the same name and world set up. Delete the duplicates in the character management to be able to connect to Sinus.",
                    NotificationType.Error));
                await StopConnectionAsync(ServerState.MultiChara).ConfigureAwait(false);
                _connectionCancellationTokenSource?.Cancel();
                return;
            }

            if (secretKey.IsNullOrEmpty())
            {
                Logger.LogWarning("No secret key set for current character");
                _connectionDto = null;
                await StopConnectionAsync(ServerState.NoSecretKey).ConfigureAwait(false);
                _connectionCancellationTokenSource?.Cancel();
                return;
            }
        }
        else
        {
            var oauth2 = _serverManager.GetOAuth2(out bool multi);
            if (multi)
            {
                Logger.LogWarning("Multiple secret keys for current character");
                _connectionDto = null;
                Mediator.Publish(new NotificationMessage("Multiple Identical Characters detected",
                    "Your Service configuration has multiple characters with the same name and world set up. Delete the duplicates in the character management to be able to connect to Sinus.",
                    NotificationType.Error));
                await StopConnectionAsync(ServerState.MultiChara).ConfigureAwait(false);
                _connectionCancellationTokenSource?.Cancel();
                return;
            }

            if (!oauth2.HasValue)
            {
                Logger.LogWarning("No UID/OAuth set for current character");
                _connectionDto = null;
                await StopConnectionAsync(ServerState.OAuthMisconfigured).ConfigureAwait(false);
                _connectionCancellationTokenSource?.Cancel();
                return;
            }

            if (!await _tokenProvider.TryUpdateOAuth2LoginTokenAsync(_serverManager.CurrentServer)
                    .ConfigureAwait(false))
            {
                Logger.LogWarning("OAuth2 login token could not be updated");
                _connectionDto = null;
                await StopConnectionAsync(ServerState.OAuthLoginTokenStale).ConfigureAwait(false);
                _connectionCancellationTokenSource?.Cancel();
                return;
            }
        }

        await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);

        Logger.LogInformation("Recreating Connection");
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController),
            Services.Events.EventSeverity.Informational,
            $"Starting Connection to {_serverManager.CurrentServer.ServerName}")));

        _connectionCancellationTokenSource?.Cancel();
        _connectionCancellationTokenSource?.Dispose();
        _connectionCancellationTokenSource = new CancellationTokenSource();
        var token = _connectionCancellationTokenSource.Token;
        while (ServerState is not ServerState.Connected && !token.IsCancellationRequested)
        {
            AuthFailureMessage = string.Empty;

            await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
            ServerState = ServerState.Connecting;

            try
            {
                Logger.LogDebug("Building connection");

                try
                {
                    _lastUsedToken = await _tokenProvider.GetOrUpdateToken(token).ConfigureAwait(false);
                }
                catch (SinusAuthFailureException ex)
                {
                    AuthFailureMessage = ex.Reason;
                    throw new HttpRequestException("Error during authentication", ex,
                        System.Net.HttpStatusCode.Unauthorized);
                }

                while (!await _dalamudUtil.GetIsPlayerPresentAsync().ConfigureAwait(false) &&
                       !token.IsCancellationRequested)
                {
                    Logger.LogDebug("Player not loaded in yet, waiting");
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }

                if (token.IsCancellationRequested) break;

                _sinusHub = _hubFactory.GetOrCreate(token);
                InitializeApiHooks();

                await _sinusHub.StartAsync(token).ConfigureAwait(false);

                _connectionDto = await GetConnectionDto().ConfigureAwait(false);

                ServerState = ServerState.Connected;

                var currentClientVer = Assembly.GetExecutingAssembly().GetName().Version!;

                if (_connectionDto.ServerVersion != ISinusHub.ApiVersion)
                {
                    if (_connectionDto.CurrentClientVersion > currentClientVer)
                    {
                        Mediator.Publish(new NotificationMessage("Client incompatible",
                            $"Your client is outdated ({currentClientVer.Major}.{currentClientVer.Minor}.{currentClientVer.Build}), current is: " +
                            $"{_connectionDto.CurrentClientVersion.Major}.{_connectionDto.CurrentClientVersion.Minor}.{_connectionDto.CurrentClientVersion.Build}. " +
                            $"This client version is incompatible and will not be able to connect. Please update your Sinus Synchronous client.",
                            NotificationType.Error));
                    }

                    await StopConnectionAsync(ServerState.VersionMisMatch).ConfigureAwait(false);
                    return;
                }

                if (_connectionDto.CurrentClientVersion > currentClientVer)
                {
                    Mediator.Publish(new NotificationMessage("Client outdated",
                        $"Your client is outdated ({currentClientVer.Major}.{currentClientVer.Minor}.{currentClientVer.Build}), current is: " +
                        $"{_connectionDto.CurrentClientVersion.Major}.{_connectionDto.CurrentClientVersion.Minor}.{_connectionDto.CurrentClientVersion.Build}. " +
                        $"Please keep your Sinus Synchronous client up-to-date.",
                        NotificationType.Warning));
                }

                if (_dalamudUtil.HasModifiedGameFiles)
                {
                    Logger.LogError("Detected modified game files on connection");
                    if (!_sinusConfigService.Current.DebugStopWhining)
                        Mediator.Publish(new NotificationMessage("Modified Game Files detected",
                            "Dalamud is reporting your FFXIV installation has modified game files. Any mods installed through TexTools will produce this message. " +
                            "Sinus Synchronous, Penumbra, and some other plugins assume your FFXIV installation is unmodified in order to work. " +
                            "Synchronization with pairs/shells can break because of this. Exit the game, open XIVLauncher, click the arrow next to Log In " +
                            "and select 'repair game files' to resolve this issue. Afterwards, do not install any mods with TexTools. Your plugin configurations will remain, as will mods enabled in Penumbra.",
                            NotificationType.Error, TimeSpan.FromSeconds(15)));
                }

                if (_dalamudUtil.IsLodEnabled && !_naggedAboutLod)
                {
                    _naggedAboutLod = true;
                    Logger.LogWarning("Model LOD is enabled during connection");
                    if (!_sinusConfigService.Current.DebugStopWhining)
                    {
                        Mediator.Publish(new NotificationMessage("Model LOD is enabled",
                            "You have \"Use low-detail models on distant objects (LOD)\" enabled. Having model LOD enabled is known to be a reason to cause " +
                            "random crashes when loading in or rendering modded pairs. Disabling LOD has a very low performance impact. Disable LOD while using Sinus: " +
                            "Go to XIV Menu -> System Configuration -> Graphics Settings and disable the model LOD option.",
                            NotificationType.Warning, TimeSpan.FromSeconds(15)));
                    }
                }

                if (_naggedAboutLod && !_dalamudUtil.IsLodEnabled)
                {
                    _naggedAboutLod = false;
                }

                await LoadIninitialPairsAsync().ConfigureAwait(false);
                await LoadOnlinePairsAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.LogWarning("Connection attempt cancelled");
                return;
            }
            catch (HttpRequestException ex)
            {
                Logger.LogWarning(ex, "HttpRequestException on Connection");

                if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    await StopConnectionAsync(ServerState.Unauthorized).ConfigureAwait(false);
                    return;
                }

                ServerState = ServerState.Reconnecting;
                Logger.LogInformation("Failed to establish connection, retrying");
                await Task.Delay(TimeSpan.FromSeconds(new Random().Next(5, 20)), token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogWarning(ex, "InvalidOperationException on connection");
                await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Exception on Connection");

                Logger.LogInformation("Failed to establish connection, retrying");
                await Task.Delay(TimeSpan.FromSeconds(new Random().Next(5, 20)), token).ConfigureAwait(false);
            }
        }
    }

    public Task CyclePauseAsync(ServerIndex serverIndex, UserData userData)
    {
        if (UseMultiConnect)
        {
            CancellationTokenSource cts = new();
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            _ = Task.Run(async () =>
            {
                await GetOrCreateForServer(serverIndex).CyclePauseAsync(serverIndex, userData).ConfigureAwait(false);
            }, cts.Token);
            return Task.CompletedTask;
        }
        else
        {
            CancellationTokenSource cts = new();
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            _ = Task.Run(async () =>
            {
                var pair = _pairManager.GetOnlineUserPairs(_serverManager.CurrentServerIndex).Single(p => p.UserPair != null && p.UserData == userData);
                var perm = pair.UserPair!.OwnPermissions;
                perm.SetPaused(paused: true);
                await UserSetPairPermissions(serverIndex, new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
                // wait until it's changed
                while (pair.UserPair!.OwnPermissions != perm)
                {
                    await Task.Delay(250, cts.Token).ConfigureAwait(false);
                    Logger.LogTrace("Waiting for permissions change for {data}", userData);
                }

                perm.SetPaused(paused: false);
                await UserSetPairPermissions(serverIndex,new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
            }, cts.Token).ContinueWith((t) => cts.Dispose());

            return Task.CompletedTask;
        }
    }

    private MultiConnectSinusClient CreateNewClient(ServerIndex serverIndex)
    {
        return new MultiConnectSinusClient(serverIndex, _serverManager, _pairManager, _dalamudUtil,
            _loggerFactory, _loggerProvider, Mediator, _multiConnectTokenService);
    }

    private MultiConnectSinusClient? GetClientForServer(ServerIndex serverIndex)
    {
        _sinusClients.TryGetValue(serverIndex, out var client);
        return client;
    }
    
    private MultiConnectSinusClient GetOrCreateForServer(ServerIndex serverIndex)
    {
        return _sinusClients.GetOrAdd(serverIndex, CreateNewClient);
    }

    private Task ConnectMultiClient(ServerIndex serverIndex)
    {

        return GetOrCreateForServer(serverIndex).CreateConnectionsAsync();
    }

    private async Task PauseAsync(UserData userData)
    {
        if (UseMultiConnect)
        {
            throw new InvalidOperationException("PauseAsync: Not supported for _multiConnect, please call multi connect sinus client instead!");
        }
        var pair = _pairManager.GetOnlineUserPairs(_serverManager.CurrentServerIndex).Single(p => p.UserPair != null && p.UserData == userData);
        var perm = pair.UserPair!.OwnPermissions;
        perm.SetPaused(paused: true);
        await UserSetPairPermissions(_serverManager.CurrentServerIndex, new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
    }

    public Task<ConnectionDto> GetConnectionDto() => GetConnectionDtoAsync(true);

    private async Task<ConnectionDto> GetConnectionDtoAsync(bool publishConnected)
    {
        var dto = await _sinusHub!.InvokeAsync<ConnectionDto>(nameof(GetConnectionDto)).ConfigureAwait(false);
        if (publishConnected) Mediator.Publish(new ConnectedMessage(dto, _serverManager.CurrentServerIndex));
        return dto;
    }

    protected override void Dispose(bool disposing)
    {
        // We can always just dispose this - even if not used
        foreach (MultiConnectSinusClient multiConnectSinusClient in _sinusClients.Values)
        {
            multiConnectSinusClient.Dispose();
        }
        _sinusClients.Clear();

        base.Dispose(disposing);

        _healthCheckTokenSource?.Cancel();
        _ = Task.Run(async () => await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false));
        _connectionCancellationTokenSource?.Cancel();
    }

    private async Task ClientHealthCheckAsync(ServerIndex serverIndex, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _sinusHub != null)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            Logger.LogDebug("Checking Client Health State");

            bool requireReconnect = await RefreshTokenAsync(serverIndex, ct).ConfigureAwait(false);

            if (requireReconnect) break;

            _ = await CheckClientHealth(serverIndex).ConfigureAwait(false);
        }
    }

    private void DalamudUtilOnLogIn()
    {
        var charaName = _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult();
        var worldId = _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult();
        var auth = _serverManager.CurrentServer.Authentications.Find(f =>
            string.Equals(f.CharacterName, charaName, StringComparison.Ordinal) && f.WorldId == worldId);
        if (auth?.AutoLogin ?? false)
        {
            Logger.LogInformation("Logging into {chara}", charaName);
            // Not called for multi connect, so we just pass current server
            _ = Task.Run(() => CreateConnectionsAsync(_serverManager.CurrentServerIndex));
        }
        else
        {
            Logger.LogInformation("Not logging into {chara}, auto login disabled", charaName);
            _ = Task.Run(async () => await StopConnectionAsync(ServerState.NoAutoLogon).ConfigureAwait(false));
        }
    }

    private void DalamudUtilOnLogOut()
    {
        _ = Task.Run(async () => await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false));
        ServerState = ServerState.Offline;
    }

    private void InitializeApiHooks()
    {
        if (_sinusHub == null) return;

        Logger.LogDebug("Initializing data");
        OnDownloadReady((guid) => _ = Client_DownloadReady(guid));
        OnReceiveServerMessage((sev, msg) => _ = Client_ReceiveServerMessage(sev, msg));
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

        OnGposeLobbyJoin((dto) => _ = Client_GposeLobbyJoin(_serverManager.CurrentServerIndex, dto));
        OnGposeLobbyLeave((dto) => _ = Client_GposeLobbyLeave(dto));
        OnGposeLobbyPushCharacterData((dto) => _ = Client_GposeLobbyPushCharacterData(dto));
        OnGposeLobbyPushPoseData((dto, data) => _ = Client_GposeLobbyPushPoseData(dto, data));
        OnGposeLobbyPushWorldData((dto, data) => _ = Client_GposeLobbyPushWorldData(dto, data));

        _healthCheckTokenSource?.Cancel();
        _healthCheckTokenSource?.Dispose();
        _healthCheckTokenSource = new CancellationTokenSource();
        // Not called for multi connect, so we just pass current server
        _ = ClientHealthCheckAsync(_serverManager.CurrentServerIndex, _healthCheckTokenSource.Token);

        _initialized = true;
    }

    private async Task LoadIninitialPairsAsync()
    {
        foreach (var entry in await GroupsGetAll().ConfigureAwait(false))
        {
            Logger.LogDebug("Group: {entry}", entry);
            _pairManager.AddGroup(entry, _serverManager.CurrentServerIndex);
        }

        foreach (var userPair in await UserGetPairedClients().ConfigureAwait(false))
        {
            Logger.LogDebug("Individual Pair: {userPair}", userPair);
            _pairManager.AddUserPair(userPair, _serverManager.CurrentServerIndex);
        }
    }

    private async Task LoadOnlinePairsAsync()
    {
        CensusDataDto? dto = null;
        if (_serverManager.SendCensusData && _lastCensus != null)
        {
            var world = await _dalamudUtil.GetWorldIdAsync().ConfigureAwait(false);
            dto = new((ushort)world, _lastCensus.RaceId, _lastCensus.TribeId, _lastCensus.Gender);
            Logger.LogDebug("Attaching Census Data: {data}", dto);
        }

        foreach (var entry in await UserGetOnlinePairs(dto).ConfigureAwait(false))
        {
            Logger.LogDebug("Pair online: {pair}", entry);
            _pairManager.MarkPairOnline(entry, _serverManager.CurrentServerIndex, sendNotif: false);
        }
    }

    private void SinusHubOnClosed(Exception? arg)
    {
        _healthCheckTokenSource?.Cancel();
        Mediator.Publish(new DisconnectedMessage(_serverManager.CurrentServerIndex));
        ServerState = ServerState.Offline;
        if (arg != null)
        {
            Logger.LogWarning(arg, "Connection closed");
        }
        else
        {
            Logger.LogInformation("Connection closed");
        }
    }

    private async Task SinusHubOnReconnectedAsync()
    {
        ServerState = ServerState.Reconnecting;
        try
        {
            InitializeApiHooks();
            _connectionDto = await GetConnectionDtoAsync(publishConnected: false).ConfigureAwait(false);
            if (_connectionDto.ServerVersion != ISinusHub.ApiVersion)
            {
                await StopConnectionAsync(ServerState.VersionMisMatch).ConfigureAwait(false);
                return;
            }

            ServerState = ServerState.Connected;
            await LoadIninitialPairsAsync().ConfigureAwait(false);
            await LoadOnlinePairsAsync().ConfigureAwait(false);
            Mediator.Publish(new ConnectedMessage(_connectionDto, _serverManager.CurrentServerIndex));
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Failure to obtain data after reconnection");
            await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
        }
    }

    private void SinusHubOnReconnecting(Exception? arg)
    {
        _doNotNotifyOnNextInfo = true;
        _healthCheckTokenSource?.Cancel();
        ServerState = ServerState.Reconnecting;
        Logger.LogWarning(arg, "Connection closed... Reconnecting");
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController),
            Services.Events.EventSeverity.Warning,
            $"Connection interrupted, reconnecting to {_serverManager.CurrentServer.ServerName}")));
    }

    private async Task<bool> RefreshTokenAsync(ServerIndex serverIndex, CancellationToken ct)
    {
        bool requireReconnect = false;
        try
        {
            var token = await _tokenProvider.GetOrUpdateToken(ct).ConfigureAwait(false);
            if (!string.Equals(token, _lastUsedToken, StringComparison.Ordinal))
            {
                Logger.LogDebug("Reconnecting due to updated token");

                _doNotNotifyOnNextInfo = true;
                await CreateConnectionsAsync(serverIndex).ConfigureAwait(false);
                requireReconnect = true;
            }
        }
        catch (SinusAuthFailureException ex)
        {
            AuthFailureMessage = ex.Reason;
            await StopConnectionAsync(ServerState.Unauthorized).ConfigureAwait(false);
            requireReconnect = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not refresh token, forcing reconnect");
            _doNotNotifyOnNextInfo = true;
            await CreateConnectionsAsync(serverIndex).ConfigureAwait(false);
            requireReconnect = true;
        }

        return requireReconnect;
    }

    private async Task StopConnectionAsync(ServerState state)
    {
        ServerState = ServerState.Disconnecting;

        Logger.LogInformation("Stopping existing connection");
        await _hubFactory.DisposeHubAsync().ConfigureAwait(false);

        if (_sinusHub is not null)
        {
            Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController),
                Services.Events.EventSeverity.Informational,
                $"Stopping existing connection to {_serverManager.CurrentServer.ServerName}")));

            _initialized = false;
            _healthCheckTokenSource?.Cancel();
            Mediator.Publish(new DisconnectedMessage(_serverManager.CurrentServerIndex));
            _sinusHub = null;
            _connectionDto = null;
        }

        ServerState = state;
    }
}
#pragma warning restore MA0040