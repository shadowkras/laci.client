using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration.Models;
using LaciSynchroni.WebAPI.SignalR;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace LaciSynchroni.WebAPI
{
    using ServerIndex = int;
    
    public class MultiConnectTokenService
    {
        private readonly ConcurrentDictionary<ServerIndex, ServerHubTokenProvider> _tokenProviders;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ServerConfigurationManager _serverConfigurationManager;
        private readonly DalamudUtilService _dalamudUtilService;
        private readonly SyncMediator _syncMediator;
        private readonly HttpClient _httpClient;
        
        public MultiConnectTokenService(HttpClient httpClient, SyncMediator syncMediator, DalamudUtilService dalamudUtilService, ILoggerFactory loggerFactory, ServerConfigurationManager serverConfigurationManager)
        {
            _httpClient = httpClient;
            _syncMediator = syncMediator;
            _dalamudUtilService = dalamudUtilService;
            _loggerFactory = loggerFactory;
            _serverConfigurationManager = serverConfigurationManager;
            _tokenProviders = new ConcurrentDictionary<ServerIndex, ServerHubTokenProvider>();
        }

        public Task<string?> GetCachedToken(ServerIndex serverIndex)
        {
            return GetTokenProvider(serverIndex).GetToken();
        }

        public Task<string?> GetOrUpdateToken(ServerIndex serverIndex, CancellationToken ct)
        {
            return GetTokenProvider(serverIndex).GetOrUpdateToken(ct);
        }

        public Task<bool> TryUpdateOAuth2LoginTokenAsync(ServerIndex serverIndex, ServerStorage currentServer, bool forced = false)
        {
            return GetTokenProvider(serverIndex).TryUpdateOAuth2LoginTokenAsync(currentServer, forced);
        }
        
        private ServerHubTokenProvider GetTokenProvider(ServerIndex serverIndex)
        {
            return _tokenProviders.GetOrAdd(serverIndex, BuildNewTokenProvider);
        }

        private ServerHubTokenProvider BuildNewTokenProvider(ServerIndex serverIndex)
        {
            return new ServerHubTokenProvider(
                _loggerFactory.CreateLogger<ServerHubTokenProvider>(),
                serverIndex,
                _serverConfigurationManager,
                _dalamudUtilService,
                _syncMediator,
                _httpClient
            );
        }
    }
}