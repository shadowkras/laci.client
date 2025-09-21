﻿using Dalamud.Plugin;
using LaciSynchroni.PlayerData.Handlers;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.SyncConfiguration.Models;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using System.Collections.Concurrent;
using System.Globalization;

namespace LaciSynchroni.Interop.Ipc;

public sealed class IpcCallerPenumbra : DisposableMediatorSubscriberBase, IIpcCaller
{
    private readonly IDalamudPluginInterface _pi;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SyncMediator _syncMediator;
    private readonly RedrawManager _redrawManager;
    private bool _shownPenumbraUnavailable = false;
    private string? _penumbraModDirectory;
    public string? ModDirectory
    {
        get => _penumbraModDirectory;
        private set
        {
            if (!string.Equals(_penumbraModDirectory, value, StringComparison.Ordinal))
            {
                _penumbraModDirectory = value;
                _syncMediator.Publish(new PenumbraDirectoryChangedMessage(_penumbraModDirectory));
            }
        }
    }

    private readonly ConcurrentDictionary<IntPtr, bool> _penumbraRedrawRequests = new();

    private readonly EventSubscriber _penumbraDispose;
    private readonly EventSubscriber<nint, string, string> _penumbraGameObjectResourcePathResolved;
    private readonly EventSubscriber _penumbraInit;
    private readonly EventSubscriber<ModSettingChange, Guid, string, bool> _penumbraModSettingChanged;
    private readonly EventSubscriber<nint, int> _penumbraObjectIsRedrawn;

    private readonly AddTemporaryMod _penumbraAddTemporaryMod;
    private readonly AssignTemporaryCollection _penumbraAssignTemporaryCollection;
    private readonly ConvertTextureFile _penumbraConvertTextureFile;
    private readonly CreateTemporaryCollection _penumbraCreateNamedTemporaryCollection;
    private readonly GetEnabledState _penumbraEnabled;
    private readonly GetPlayerMetaManipulations _penumbraGetMetaManipulations;
    private readonly RedrawObject _penumbraRedraw;
    private readonly DeleteTemporaryCollection _penumbraRemoveTemporaryCollection;
    private readonly RemoveTemporaryMod _penumbraRemoveTemporaryMod;
    private readonly GetModDirectory _penumbraResolveModDir;
    private readonly ResolvePlayerPathsAsync _penumbraResolvePaths;
    private readonly GetGameObjectResourcePaths _penumbraResourcePaths;

    public IpcCallerPenumbra(ILogger<IpcCallerPenumbra> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil,
        SyncMediator syncMediator, RedrawManager redrawManager) : base(logger, syncMediator)
    {
        _pi = pi;
        _dalamudUtil = dalamudUtil;
        _syncMediator = syncMediator;
        _redrawManager = redrawManager;
        _penumbraInit = Initialized.Subscriber(pi, PenumbraInit);
        _penumbraDispose = Disposed.Subscriber(pi, PenumbraDispose);
        _penumbraResolveModDir = new GetModDirectory(pi);
        _penumbraRedraw = new RedrawObject(pi);
        _penumbraObjectIsRedrawn = GameObjectRedrawn.Subscriber(pi, RedrawEvent);
        _penumbraGetMetaManipulations = new GetPlayerMetaManipulations(pi);
        _penumbraRemoveTemporaryMod = new RemoveTemporaryMod(pi);
        _penumbraAddTemporaryMod = new AddTemporaryMod(pi);
        _penumbraCreateNamedTemporaryCollection = new CreateTemporaryCollection(pi);
        _penumbraRemoveTemporaryCollection = new DeleteTemporaryCollection(pi);
        _penumbraAssignTemporaryCollection = new AssignTemporaryCollection(pi);
        _penumbraResolvePaths = new ResolvePlayerPathsAsync(pi);
        _penumbraEnabled = new GetEnabledState(pi);
        _penumbraModSettingChanged = ModSettingChanged.Subscriber(pi, (change, arg1, arg, b) =>
        {
            if (change == ModSettingChange.EnableState)
                _syncMediator.Publish(new PenumbraModSettingChangedMessage());
        });
        _penumbraConvertTextureFile = new ConvertTextureFile(pi);
        _penumbraResourcePaths = new GetGameObjectResourcePaths(pi);

        _penumbraGameObjectResourcePathResolved = GameObjectResourcePathResolved.Subscriber(pi, ResourceLoaded);

        CheckAPI();
        CheckModDirectory();

        Mediator.Subscribe<PenumbraRedrawCharacterMessage>(this, (msg) =>
        {
            _penumbraRedraw.Invoke(msg.Character.ObjectIndex, RedrawType.AfterGPose);
        });

        Mediator.Subscribe<DalamudLoginMessage>(this, (msg) => _shownPenumbraUnavailable = false);
    }

    public bool APIAvailable { get; private set; } = false;

    public void CheckAPI()
    {
        bool penumbraAvailable = false;
        try
        {
            var penumbraVersion = (_pi.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, "Penumbra", StringComparison.OrdinalIgnoreCase))
                ?.Version ?? new Version(0, 0, 0, 0));
            penumbraAvailable = penumbraVersion >= new Version(1, 2, 0, 22);
            try
            {
                penumbraAvailable &= _penumbraEnabled.Invoke();
            }
            catch
            {
                penumbraAvailable = false;
            }
            _shownPenumbraUnavailable = _shownPenumbraUnavailable && !penumbraAvailable;
            APIAvailable = penumbraAvailable;
        }
        catch
        {
            APIAvailable = penumbraAvailable;
        }
        finally
        {
            if (!penumbraAvailable && !_shownPenumbraUnavailable)
            {
                _shownPenumbraUnavailable = true;
                _syncMediator.Publish(new NotificationMessage("Penumbra inactive",
                    "Your Penumbra installation is not active or out of date." +
                    $" Update Penumbra and/or the Enable Mods setting in Penumbra to continue to use {_dalamudUtil.GetPluginName()}. If you just updated Penumbra, ignore this message.",
                    NotificationType.Error));
            }
        }
    }

    public void CheckModDirectory()
    {
        if (!APIAvailable)
        {
            ModDirectory = string.Empty;
        }
        else
        {
            ModDirectory = _penumbraResolveModDir!.Invoke().ToLowerInvariant();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _redrawManager.Cancel();

        _penumbraModSettingChanged.Dispose();
        _penumbraGameObjectResourcePathResolved.Dispose();
        _penumbraDispose.Dispose();
        _penumbraInit.Dispose();
        _penumbraObjectIsRedrawn.Dispose();
    }

    public async Task AssignTemporaryCollectionAsync(ILogger logger, Guid collName, int idx)
    {
        if (!APIAvailable) return;

        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            var retAssign = _penumbraAssignTemporaryCollection.Invoke(collName, idx, forceAssignment: true);
            logger.LogTrace("Assigning Temp Collection {CollName} to index {Idx}, Success: {Ret}", collName, idx, retAssign);
            return collName;
        }).ConfigureAwait(false);
    }

    public async Task ConvertTextureFiles(ILogger logger, IDictionary<string, string[]> textures, IProgress<(string, int)> progress, CancellationToken token)
    {
        if (!APIAvailable) return;

        _syncMediator.Publish(new HaltScanMessage(nameof(ConvertTextureFiles)));
        int currentTexture = 0;
        foreach (var texture in textures)
        {
            if (token.IsCancellationRequested) break;

            progress.Report((texture.Key, ++currentTexture));

            logger.LogInformation("Converting Texture {Key} to {Type}", texture.Key, TextureType.Bc7Tex);
            var convertTask = _penumbraConvertTextureFile.Invoke(texture.Key, texture.Key, TextureType.Bc7Tex, mipMaps: true);
            await convertTask.ConfigureAwait(false);
            if (convertTask.IsCompletedSuccessfully && texture.Value.Any())
            {
                foreach (var duplicatedTexture in texture.Value)
                {
                    logger.LogInformation("Migrating duplicate {Duplicate}", duplicatedTexture);
                    if (IsFileLocked(texture.Key))
                    {
                        logger.LogInformation("Skipped duplicate {Duplicate} because the source file is in use.", duplicatedTexture);
                        continue;
                    }
                    try
                    {   
                        File.Copy(texture.Key, duplicatedTexture, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to copy duplicate {Duplicate}", duplicatedTexture);
                    }
                }
            }
        }
        _syncMediator.Publish(new ResumeScanMessage(nameof(ConvertTextureFiles)));

        await _dalamudUtil.RunOnFrameworkThread(async () =>
        {
            var gameObject = await _dalamudUtil.CreateGameObjectAsync(await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false)).ConfigureAwait(false);
            _penumbraRedraw.Invoke(gameObject!.ObjectIndex, setting: RedrawType.Redraw);
        }).ConfigureAwait(false);
    }

    public async Task<Guid> CreateTemporaryCollectionAsync(ILogger logger, string uid)
    {
        if (!APIAvailable) return Guid.Empty;

        return await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            var collName = "Laci_" + uid;
            var _error = _penumbraCreateNamedTemporaryCollection.Invoke(_dalamudUtil.PluginInternalName, collName, out var collId);

            if (_error != PenumbraApiEc.Success)
            {
                logger.LogError("Failed to create temporary collection for {Collname}. Penumbra returned the error code {_error} ({_errorCode}).", collName, nameof(_error), _error);
                return Guid.Empty;
            }

            logger.LogTrace("Creating Temp Collection {CollName}, GUID: {CollId}", collName, collId);
            return collId;

        }).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, HashSet<string>>?> GetCharacterData(ILogger logger, GameObjectHandler handler)
    {
        if (!APIAvailable) return null;

        return await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            logger.LogTrace("Calling On IPC: Penumbra.GetGameObjectResourcePaths");
            var idx = handler.GetGameObject()?.ObjectIndex;
            if (idx == null) return null;
            return _penumbraResourcePaths.Invoke(idx.Value)[0];
        }).ConfigureAwait(false);
    }

    public string GetMetaManipulations()
    {
        if (!APIAvailable) return string.Empty;
        return _penumbraGetMetaManipulations.Invoke();
    }

    public async Task RedrawAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        if (!APIAvailable || _dalamudUtil.IsZoning) return;
        try
        {
            await _redrawManager.RedrawSemaphore.WaitAsync(token).ConfigureAwait(false);
            await _redrawManager.PenumbraRedrawInternalAsync(logger, handler, applicationId, (chara) =>
            {
                logger.LogDebug("[{Appid}] Calling on IPC: PenumbraRedraw", applicationId);
                _penumbraRedraw!.Invoke(chara.ObjectIndex, setting: RedrawType.Redraw);

            }, token).ConfigureAwait(false);
        }
        finally
        {
            _redrawManager.RedrawSemaphore.Release();
        }
    }

    public async Task RemoveTemporaryCollectionAsync(ILogger logger, Guid applicationId, Guid collId)
    {
        if (!APIAvailable) return;
        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            logger.LogTrace("[{ApplicationId}] Removing temp collection for {CollId}", applicationId, collId);
            var ret2 = _penumbraRemoveTemporaryCollection.Invoke(collId);
            logger.LogTrace("[{ApplicationId}] RemoveTemporaryCollection: {Ret2}", applicationId, ret2);
        }).ConfigureAwait(false);
    }

    public async Task<(string[] forward, string[][] reverse)> ResolvePathsAsync(string[] forward, string[] reverse)
    {
        return await _penumbraResolvePaths.Invoke(forward, reverse).ConfigureAwait(false);
    }

    public async Task SetManipulationDataAsync(ILogger logger, Guid applicationId, Guid collId, string manipulationData)
    {
        if (!APIAvailable) return;

        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            logger.LogTrace("[{ApplicationId}] Manip: {Data}", applicationId, manipulationData);
            var retAdd = _penumbraAddTemporaryMod.Invoke("LaciChara_Meta", collId, [], manipulationData, 0);
            logger.LogTrace("[{ApplicationId}] Setting temp meta mod for {CollId}, Success: {Ret}", applicationId, collId, retAdd);
        }).ConfigureAwait(false);
    }

    public async Task SetTemporaryModsAsync(ILogger logger, Guid applicationId, Guid collId, Dictionary<string, string> modPaths)
    {
        if (!APIAvailable) return;

        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            foreach (var mod in modPaths)
            {
                logger.LogTrace("[{ApplicationId}] Change: {From} => {To}", applicationId, mod.Key, mod.Value);
            }
            var retRemove = _penumbraRemoveTemporaryMod.Invoke("LaciChara_Files", collId, 0);
            logger.LogTrace("[{ApplicationId}] Removing temp files mod for {CollId}, Success: {Ret}", applicationId, collId, retRemove);
            var retAdd = _penumbraAddTemporaryMod.Invoke("LaciChara_Files", collId, modPaths, string.Empty, 0);
            logger.LogTrace("[{ApplicationId}] Setting temp files mod for {CollId}, Success: {Ret}", applicationId, collId, retAdd);
        }).ConfigureAwait(false);
    }

    private void RedrawEvent(IntPtr objectAddress, int objectTableIndex)
    {
        bool wasRequested = false;
        if (_penumbraRedrawRequests.TryGetValue(objectAddress, out var redrawRequest) && redrawRequest)
        {
            _penumbraRedrawRequests[objectAddress] = false;
        }
        else
        {
            _syncMediator.Publish(new PenumbraRedrawMessage(objectAddress, objectTableIndex, wasRequested));
        }
    }

    private void ResourceLoaded(IntPtr ptr, string arg1, string arg2)
    {
        if (ptr != IntPtr.Zero && string.Compare(arg1, arg2, ignoreCase: true, CultureInfo.InvariantCulture) != 0)
        {
            _syncMediator.Publish(new PenumbraResourceLoadMessage(ptr, arg1, arg2));
        }
    }

    private static bool IsFileLocked(string path)
    {
        try
        {
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                return false;
            }
        }
        catch (IOException)
        {
            return true;
        }
    }

    private void PenumbraDispose()
    {
        _redrawManager.Cancel();
        _syncMediator.Publish(new PenumbraDisposedMessage());
    }

    private void PenumbraInit()
    {
        APIAvailable = true;
        ModDirectory = _penumbraResolveModDir.Invoke();
        _syncMediator.Publish(new PenumbraInitializedMessage());
        _penumbraRedraw!.Invoke(0, setting: RedrawType.Redraw);
    }
}