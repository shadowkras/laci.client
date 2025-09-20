using LaciSynchroni.Common.Routes;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration.Models;
using LaciSynchroni.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;

namespace LaciSynchroni.WebAPI.SignalR;

public sealed class ServerHubTokenProvider : IDisposable, IMediatorSubscriber
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ServerConfigurationManager _serverManager;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ServerHubTokenProvider> _logger;
    private readonly int _serverIndex;
    private readonly ConcurrentDictionary<JwtIdentifier, string> _tokenCache = new();

    private ServerStorage ServerToUse => _serverManager.GetServerByIndex(_serverIndex);
    private string ServerName => ServerToUse?.ServerName ?? "Unknown service";

    public ServerHubTokenProvider(ILogger<ServerHubTokenProvider> logger, int serverIndex,
        ServerConfigurationManager serverManager, DalamudUtilService dalamudUtil, SyncMediator syncMediator,
        HttpClient httpClient)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _serverManager = serverManager;
        _serverIndex = serverIndex;
        Mediator = syncMediator;
        _httpClient = httpClient;
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) =>
        {
            _lastJwtIdentifier = null;
            _tokenCache.Clear();
        });
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) =>
        {
            _lastJwtIdentifier = null;
            _tokenCache.Clear();
        });
    }

    public SyncMediator Mediator { get; }

    private JwtIdentifier? _lastJwtIdentifier;

    public void Dispose()
    {
        Mediator.UnsubscribeAll(this);
    }

    public async Task<string> GetNewToken(bool isRenewal, JwtIdentifier identifier, CancellationToken ct)
    {
        Uri tokenUri;
        string response = string.Empty;
        HttpResponseMessage result;

        try
        {
            if (!isRenewal)
            {
                _logger.LogDebug("GetNewToken: Requesting token for {ServerName}", ServerName);

                if (!ServerToUse.UseOAuth2)
                {
                    tokenUri = AuthRoutes.AuthFullPath(new Uri(ServerToUse.ServerUri
                        .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                        .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)));
                    var secretKey = _serverManager.GetSecretKey(out _, _serverIndex)!;
                    var auth = secretKey.GetHash256();
                    _logger.LogInformation("Sending SecretKey Request to server {ServerName} with auth {Auth}", ServerName,
                        string.Join("", identifier.SecretKeyOrOAuth.Take(10)));
                    result = await _httpClient.PostAsync(tokenUri, new FormUrlEncodedContent(
                    [
                        new KeyValuePair<string, string>("auth", auth),
                        new KeyValuePair<string, string>("charaIdent",
                            await _dalamudUtil.GetPlayerNameHashedAsync().ConfigureAwait(false)),
                    ]), ct).ConfigureAwait(false);
                }
                else
                {
                    tokenUri = AuthRoutes.AuthWithOauthFullPath(new Uri(ServerToUse.ServerUri
                        .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                        .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)));
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, tokenUri.ToString());
                    request.Content = new FormUrlEncodedContent([
                        new KeyValuePair<string, string>("uid", identifier.UID),
                        new KeyValuePair<string, string>("charaIdent", identifier.CharaHash)
                    ]);
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", identifier.SecretKeyOrOAuth);
                    _logger.LogInformation("Sending OAuth Request to server {ServerName} with auth {Auth}", ServerName,
                        string.Join("", identifier.SecretKeyOrOAuth.Take(10)));
                    result = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                }
            }
            else
            {
                _logger.LogDebug("GetNewToken: Renewal for {ServerName}", ServerName);

                tokenUri = AuthRoutes.RenewTokenFullPath(new Uri(ServerToUse.ServerUri
                    .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                    .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)));
                HttpRequestMessage request = new(HttpMethod.Get, tokenUri.ToString());
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenCache[identifier]);
                result = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            }

            response = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
            result.EnsureSuccessStatusCode();
            _tokenCache[identifier] = response;
        }
        catch (HttpRequestException ex)
        {
            _tokenCache.TryRemove(identifier, out _);

            _logger.LogError(ex, "GetNewToken: Failure to get token from {ServerName}", ServerName);

            if (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (isRenewal)
                    Mediator.Publish(new NotificationMessage($"Error refreshing token from {ServerName}",
                        $"Your authentication token could not be renewed. Try reconnecting to a {_dalamudUtil.GetPluginName()} server manually.",
                        NotificationType.Error));
                else
                    Mediator.Publish(new NotificationMessage($"Error generating token from {ServerName}",
                        $"Your authentication token could not be generated. Check {_dalamudUtil.GetPluginName()} Main UI ({CommandManagerService.CommandName} in chat) to see the error message.",
                        NotificationType.Error));
                Mediator.Publish(new DisconnectedMessage(_serverIndex));
                throw new SyncAuthFailureException(response);
            }

            throw;
        }

        var handler = new JwtSecurityTokenHandler();
        var jwtToken = handler.ReadJwtToken(response);
        _logger.LogTrace("GetNewToken: JWT {Token}", response);
        _logger.LogDebug("GetNewToken: Valid until {ValidTo}, ValidClaim until {Date}", jwtToken.ValidTo,
            new DateTime(
                long.Parse(jwtToken.Claims
                    .Single(c => string.Equals(c.Type, "expiration_date", StringComparison.Ordinal)).Value),
                DateTimeKind.Utc));
        var dateTimeMinus10 = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(10));
        var dateTimePlus10 = DateTime.UtcNow.Add(TimeSpan.FromMinutes(10));
        var tokenTime = jwtToken.ValidTo.Subtract(TimeSpan.FromHours(6));
        if (tokenTime <= dateTimeMinus10 || tokenTime >= dateTimePlus10)
        {
            _tokenCache.TryRemove(identifier, out _);
            Mediator.Publish(new NotificationMessage("Invalid system clock", "The clock of your computer is invalid. " +
                                                                             $"{_dalamudUtil.GetPluginName()} will not function properly if the time zone is not set correctly. " +
                                                                             "Please set your computers time zone correctly and keep your clock synchronized with the internet.",
                NotificationType.Error));
            throw new InvalidOperationException(
                $"JwtToken is behind DateTime.UtcNow, DateTime.UtcNow is possibly wrong. DateTime.UtcNow is {DateTime.UtcNow}, JwtToken.ValidTo is {jwtToken.ValidTo}");
        }

        return response;
    }

    private async Task<JwtIdentifier?> GetIdentifier()
    {
        JwtIdentifier jwtIdentifier;
        try
        {
            var playerIdentifier = await _dalamudUtil.GetPlayerNameHashedAsync().ConfigureAwait(false);

            if (string.IsNullOrEmpty(playerIdentifier))
            {
                _logger.LogTrace("GetIdentifier: PlayerIdentifier was null, returning last identifier {Identifier}",
                    _lastJwtIdentifier);
                return _lastJwtIdentifier;
            }

            if (ServerToUse.UseOAuth2)
            {
                var (OAuthToken, UID) = _serverManager.GetOAuth2(out _, _serverIndex)
                                        ?? throw new InvalidOperationException("Requested OAuth2 but received null");

                jwtIdentifier = new(ServerToUse.ServerUri,
                    playerIdentifier,
                    UID, OAuthToken);
            }
            else
            {
                var secretKey = _serverManager.GetSecretKey(out _, _serverIndex) ??
                                throw new InvalidOperationException($"Requested SecretKey from {ServerName} but received null");

                jwtIdentifier = new(ServerToUse.ServerUri,
                    playerIdentifier,
                    string.Empty,
                    secretKey);
            }

            _lastJwtIdentifier = jwtIdentifier;
        }
        catch (Exception ex)
        {
            if (_lastJwtIdentifier == null)
            {
                _logger.LogError("GetIdentifier: No last identifier found, aborting");
                return null;
            }

            _logger.LogWarning(ex,
                "GetIdentifier: Could not get JwtIdentifier for some reason or another, reusing last identifier {Identifier}",
                _lastJwtIdentifier);
            jwtIdentifier = _lastJwtIdentifier;
        }

        _logger.LogDebug("GetIdentifier: Using identifier {Identifier}", jwtIdentifier);
        return jwtIdentifier;
    }

    public async Task<string?> GetToken()
    {
        JwtIdentifier? jwtIdentifier = await GetIdentifier().ConfigureAwait(false);
        if (jwtIdentifier == null) return null;

        if (_tokenCache.TryGetValue(jwtIdentifier, out var token))
        {
            return token;
        }

        throw new InvalidOperationException("No token present");
    }

    public async Task<string?> GetOrUpdateToken(CancellationToken ct)
    {
        JwtIdentifier? jwtIdentifier = await GetIdentifier().ConfigureAwait(false);
        if (jwtIdentifier == null) return null;

        bool renewal = false;
        if (_tokenCache.TryGetValue(jwtIdentifier, out var token))
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);
            if (jwt.ValidTo == DateTime.MinValue || jwt.ValidTo.Subtract(TimeSpan.FromMinutes(5)) > DateTime.UtcNow)
            {
                return token;
            }

            _logger.LogDebug(
                "GetOrUpdate: Cached token for {ServerName} requires renewal, token valid to: {Valid}, UtcTime is {UtcTime}", ServerName,
                jwt.ValidTo, DateTime.UtcNow);
            renewal = true;
        }
        else
        {
            _logger.LogDebug("GetOrUpdate: Did not find token for {ServerName} in cache, requesting a new one", ServerName);
        }

        _logger.LogTrace("GetOrUpdate: Getting new token for {ServerName}", ServerName);
        return await GetNewToken(renewal, jwtIdentifier, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryUpdateOAuth2LoginTokenAsync(ServerStorage currentServer, bool forced = false)
    {
        var oauth2 = _serverManager.GetOAuth2(out _, _serverIndex);
        if (oauth2 == null) return false;

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(oauth2.Value.OAuthToken);
        if (!forced)
        {
            if (jwt.ValidTo == DateTime.MinValue || jwt.ValidTo.Subtract(TimeSpan.FromDays(7)) > DateTime.Now)
                return true;

            if (jwt.ValidTo < DateTime.UtcNow)
                return false;
        }

        var tokenUri = AuthRoutes.RenewOAuthTokenFullPath(new Uri(currentServer.ServerUri
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)));
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, tokenUri.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauth2.Value.OAuthToken);
        _logger.LogInformation("Sending Request to server {ServerName} with auth {Auth}", ServerName,
            string.Join("", oauth2.Value.OAuthToken.Take(10)));
        var result = await _httpClient.SendAsync(request).ConfigureAwait(false);

        if (!result.IsSuccessStatusCode)
        {
            _logger.LogWarning("Could not renew OAuth2 Login token for {ServerName}, error code {Error}", ServerName, result.StatusCode);
            currentServer.OAuthToken = null;
            _serverManager.Save();
            return false;
        }

        var newToken = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
        currentServer.OAuthToken = newToken;
        _serverManager.Save();

        return true;
    }
}