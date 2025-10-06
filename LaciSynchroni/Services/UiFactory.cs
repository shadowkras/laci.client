using LaciSynchroni.Common.Dto.Group;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.UI;
using LaciSynchroni.UI.Components.Popup;
using LaciSynchroni.WebAPI;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.Services;

public class UiFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SyncMediator _syncMediator;
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly ProfileManager _profileManager;
    private readonly PerformanceCollectorService _performanceCollectorService;

    public UiFactory(ILoggerFactory loggerFactory, SyncMediator syncMediator, ApiController apiController,
        UiSharedService uiSharedService, PairManager pairManager, ServerConfigurationManager serverConfigManager,
        ProfileManager profileManager, PerformanceCollectorService performanceCollectorService)
    {
        _loggerFactory = loggerFactory;
        _syncMediator = syncMediator;
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _serverConfigManager = serverConfigManager;
        _profileManager = profileManager;
        _performanceCollectorService = performanceCollectorService;
    }

    public SyncshellAdminUI CreateSyncshellAdminUi(GroupFullInfoDto dto, int serverIndex)
    {
        return new SyncshellAdminUI(_loggerFactory.CreateLogger<SyncshellAdminUI>(), _syncMediator,
            _apiController, _uiSharedService, _pairManager, dto, _performanceCollectorService, serverIndex);
    }

    public StandaloneProfileUi CreateStandaloneProfileUi(IEnumerable<Pair> pairs)
    {
        return new StandaloneProfileUi(_loggerFactory.CreateLogger<StandaloneProfileUi>(), _syncMediator,
            _uiSharedService, _serverConfigManager, _profileManager, _pairManager, pairs, _apiController, _performanceCollectorService);
    }

    public PermissionWindowUI CreatePermissionPopupUi(IEnumerable<Pair> pairs)
    {
        return new PermissionWindowUI(_loggerFactory.CreateLogger<PermissionWindowUI>(), pairs,
            _syncMediator, _uiSharedService, _serverConfigManager, _apiController, _performanceCollectorService);
    }
}
