﻿using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using LaciSynchroni.Common.Data;
using LaciSynchroni.Common.Data.Comparer;
using LaciSynchroni.Common.Data.Enum;
using LaciSynchroni.Common.Data.Extensions;
using LaciSynchroni.Common.Dto.Group;
using LaciSynchroni.Common.Dto.User;
using LaciSynchroni.PlayerData.Factories;
using LaciSynchroni.Services.Events;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.SyncConfiguration;
using LaciSynchroni.SyncConfiguration.Models;
using LaciSynchroni.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace LaciSynchroni.PlayerData.Pairs;

public sealed class PairManager : DisposableMediatorSubscriberBase
{
    private readonly ConcurrentDictionary<ServerBasedUserKey, Pair> _allClientPairs =
        new(ServerBasedUserKeyComparator.Instance);

    private readonly ConcurrentDictionary<ServerBasedGroupKey, GroupFullInfoDto> _allGroups =
        new(ServerBasedGroupKeyComparator.Instance);

    private readonly SyncConfigService _configurationService;
    private readonly IContextMenu _dalamudContextMenu;
    private readonly PairFactory _pairFactory;
    private Lazy<List<Pair>> _directPairsInternal;
    private Lazy<Dictionary<GroupFullInfoWithServer, List<Pair>>> _groupPairsInternal;
    private Lazy<Dictionary<Pair, List<GroupFullInfoDto>>> _pairsWithGroupsInternal;

    public PairManager(ILogger<PairManager> logger, PairFactory pairFactory,
        SyncConfigService configurationService, SyncMediator mediator,
        IContextMenu dalamudContextMenu) : base(logger, mediator)
    {
        _pairFactory = pairFactory;
        _configurationService = configurationService;
        _dalamudContextMenu = dalamudContextMenu;
        Mediator.Subscribe<DisconnectedMessage>(this, (msg) => ClearPairs(msg.ServerIndex));
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => ReapplyPairData());
        _directPairsInternal = DirectPairsLazy();
        _groupPairsInternal = GroupPairsLazy();
        _pairsWithGroupsInternal = PairsWithGroupsLazy();

        _dalamudContextMenu.OnMenuOpened += DalamudContextMenuOnOnOpenGameObjectContextMenu;
    }

    public List<Pair> DirectPairs => _directPairsInternal.Value;

    public Dictionary<GroupFullInfoWithServer, List<Pair>> GroupPairs => _groupPairsInternal.Value;
    public Dictionary<ServerBasedGroupKey, GroupFullInfoDto> Groups => _allGroups.ToDictionary(k => k.Key, k => k.Value);
    public Pair? LastAddedUser { get; internal set; }
    public Dictionary<Pair, List<GroupFullInfoDto>> PairsWithGroups => _pairsWithGroupsInternal.Value;

    public void AddGroup(GroupFullInfoDto dto, int serverIndex)
    {
        var key = BuildKey(dto.Group, serverIndex);
        _allGroups[key] = dto;
        RecreateLazy();
    }

    public void AddGroupPair(GroupPairFullInfoDto dto, int serverIndex)
    {
        var key = BuildKey(dto.User, serverIndex);
        if (!_allClientPairs.ContainsKey(key))
            _allClientPairs[key] = _pairFactory.Create(new UserFullPairDto(dto.User,
                IndividualPairStatus.None,
                [dto.Group.GID], dto.SelfToOtherPermissions, dto.OtherToSelfPermissions), serverIndex);
        else _allClientPairs[key].UserPair.Groups.Add(dto.GID);
        RecreateLazy();
    }

    public Pair? GetPairByUID(int serverIndex, string uid)
    {
        var existingPair =
            _allClientPairs.FirstOrDefault(f => string.Equals(f.Key.UserData.UID, uid, StringComparison.Ordinal) && f.Key.ServerIndex == serverIndex);
        if (!Equals(existingPair, default(KeyValuePair<ServerBasedUserKey, Pair>)))
        {
            return existingPair.Value;
        }

        return null;
    }

    public void AddUserPair(UserFullPairDto dto, int serverIndex)
    {
        var key = BuildKey(dto.User, serverIndex);
        if (!_allClientPairs.ContainsKey(key))
        {
            _allClientPairs[key] = _pairFactory.Create(dto, serverIndex);
        }
        else
        {
            _allClientPairs[key].UserPair.IndividualPairStatus = dto.IndividualPairStatus;
            _allClientPairs[key].ApplyLastReceivedData();
        }

        RecreateLazy();
    }

    public void AddUserPair(UserPairDto dto, int serverIndex, bool addToLastAddedUser = true)
    {
        var key = BuildKey(dto.User, serverIndex);
        if (!_allClientPairs.ContainsKey(key))
        {
            _allClientPairs[key] = _pairFactory.Create(dto, serverIndex);
        }
        else
        {
            addToLastAddedUser = false;
        }

        _allClientPairs[key].UserPair.IndividualPairStatus = dto.IndividualPairStatus;
        _allClientPairs[key].UserPair.OwnPermissions = dto.OwnPermissions;
        _allClientPairs[key].UserPair.OtherPermissions = dto.OtherPermissions;
        if (addToLastAddedUser)
            LastAddedUser = _allClientPairs[key];
        _allClientPairs[key].ApplyLastReceivedData();
        RecreateLazy();
    }

    public void ClearPairs(int serverIndex)
    {
        Logger.LogDebug("Clearing all Pairs");
        DisposePairs(serverIndex);
        _allClientPairs.Keys
            .Where(key => key.ServerIndex == serverIndex)
            .ToList()
            .ForEach(key => _allClientPairs.Remove(key, out _));
        _allGroups.Keys
            .Where(key => key.ServerIndex == serverIndex)
            .ToList()
            .ForEach(key => _allGroups.Remove(key, out _));
        RecreateLazy();
    }

    public List<Pair> GetOnlineUserPairs(int serverIndex) => _allClientPairs
        .Where(p => p.Key.ServerIndex == serverIndex)
        .Where(p => !string.IsNullOrEmpty(p.Value.GetPlayerNameHash())).Select(p => p.Value).ToList();

    public List<Pair> GetOnlineUserPairsAcrossAllServers() => _allClientPairs
        .Where(p => !string.IsNullOrEmpty(p.Value.GetPlayerNameHash())).Select(p => p.Value).ToList();

    public IEnumerable<string> GetVisibleUserPlayerNameOrNotesFromServer(int serverIndex) => GetOnlineUserPairs(serverIndex)
        .Where(x => x.IsVisible)
        .Select(x => string.Format("{0} ({1})", _configurationService.Current.PreferNoteInDtrTooltip ? x.GetNote() ?? x.PlayerName : x.PlayerName, x.UserData.AliasOrUID));

    public IEnumerable<string> GetVisibleUserPlayerNameOrNotesAcrossAllServers(bool showUid)
    {
        var preferNotes = _configurationService.Current.PreferNoteInDtrTooltip;
        var visibleUserPairs = _allClientPairs
            .Where(p => !string.IsNullOrEmpty(p.Value.GetPlayerNameHash()) && p.Value.IsVisible)
            .Select(p => new
            {
                p.Value.PlayerName,
                p.Value.UserData.AliasOrUID,
                Note = preferNotes ? p.Value.GetNote() : string.Empty,
            })
            .AsEnumerable();

        if (showUid)
        {
            return visibleUserPairs
                .GroupBy(x => preferNotes ? x.Note ?? x.PlayerName : x.PlayerName, StringComparer.InvariantCulture)
                .Select(g => string.Format("{0} ({1})", g.Key, string.Join(" | ", g.Select(x => x.AliasOrUID).Distinct(StringComparer.InvariantCulture))));
        }
        else
        {
            return visibleUserPairs
                .DistinctBy(p=> p.PlayerName)
                .Select(x => string.Format("{0}", preferNotes ? x.Note ?? x.PlayerName : x.PlayerName));
        }
    }

    public int GetVisibleUserCountAcrossAllServers() => _allClientPairs
        .Count(p => p.Value.IsVisible);

    public int GetVisibleUserCount(int serverIndex) => _allClientPairs.Where(p=> p.Key.ServerIndex == serverIndex).Count(p => p.Value.IsVisible);

    public List<ServerBasedUserKey> GetVisibleUsers(int serverIndex) =>
    [
        .. _allClientPairs
            .Where(p => p.Key.ServerIndex == serverIndex && p.Value.IsVisible)
            .Select(p => p.Key)
    ];

    public List<ServerBasedUserKey> GetVisibleUsersAcrossAllServers() =>
    [
        .. _allClientPairs
            .Where(p => p.Value.IsVisible)
            .Select(p => p.Key)
    ];

    public void MarkPairOffline(UserData user, int serverIndex)
    {
        var key = BuildKey(user, serverIndex);
        if (_allClientPairs.TryGetValue(key, out var pair))
        {
            // Gets cleared when the pair is marked offline, but we need it after for the redraw
            var removedPairName = pair.PlayerName;
            var message = new ServerBasedUserKey(user, serverIndex);
            Mediator.Publish(new ClearProfileDataMessage(message));
            pair.MarkOffline();
            RedrawStillVisiblePairs(pair, removedPairName);
        }

        RecreateLazy();
    }

    public void MarkPairOnline(OnlineUserIdentDto dto, int serverIndex, bool sendNotif = true)
    {
        var key = BuildKey(dto.User, serverIndex);
        if (!_allClientPairs.ContainsKey(key)) throw new InvalidOperationException("No user found for " + dto);

        var message = new ServerBasedUserKey(dto.User, serverIndex);
        Mediator.Publish(new ClearProfileDataMessage(message));

        var pair = _allClientPairs[key];
        if (pair.HasCachedPlayer)
        {
            RecreateLazy();
            return;
        }

        if (sendNotif && _configurationService.Current.ShowOnlineNotifications
                      && (_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs &&
                          pair.IsDirectlyPaired && !pair.IsOneSidedPair
                          || !_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs)
                      && (_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs &&
                          !string.IsNullOrEmpty(pair.GetNote())
                          || !_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs))
        {
            string? note = pair.GetNote();
            var msg = !string.IsNullOrEmpty(note)
                ? $"{note} ({pair.UserData.AliasOrUID}) is now online"
                : $"{pair.UserData.AliasOrUID} is now online";
            Mediator.Publish(
                new NotificationMessage("User online", msg, NotificationType.Info, TimeSpan.FromSeconds(5)));
        }

        pair.CreateCachedPlayer(dto);

        RecreateLazy();
    }

    public void ReceiveCharaData(OnlineUserCharaDataDto dto, int serverIndex)
    {
        var key = BuildKey(dto.User, serverIndex);
        if (!_allClientPairs.TryGetValue(key, out var pair))
            throw new InvalidOperationException("No user found for " + dto.User);

        Mediator.Publish(new EventMessage(new Event(pair.UserData, GetType().Name, EventSeverity.Informational,
            "Received Character Data")));
        _allClientPairs[key].ApplyData(dto);
    }

    public void RemoveGroup(GroupData data, int serverIndex)
    {
        var key = BuildKey(data, serverIndex);
        _allGroups.TryRemove(key, out _);

        foreach (var item in _allClientPairs.ToList())
        {
            item.Value.UserPair.Groups.Remove(data.GID);

            if (!item.Value.HasAnyConnection())
            {
                item.Value.MarkOffline();
                _allClientPairs.TryRemove(item.Key, out _);
            }
        }

        RecreateLazy();
    }

    public void RemoveGroupPair(GroupPairDto dto, int serverIndex)
    {
        var key = BuildKey(dto.User, serverIndex);
        if (_allClientPairs.TryGetValue(key, out var pair))
        {
            pair.UserPair.Groups.Remove(dto.Group.GID);

            if (!pair.HasAnyConnection())
            {
                pair.MarkOffline();
                _allClientPairs.TryRemove(key, out _);
            }
        }

        RecreateLazy();
    }

    public void RemoveUserPair(UserDto dto, int serverIndex)
    {
        var key = BuildKey(dto.User, serverIndex);
        if (_allClientPairs.TryGetValue(key, out var pair))
        {
            pair.UserPair.IndividualPairStatus = IndividualPairStatus.None;

            if (!pair.HasAnyConnection())
            {
                pair.MarkOffline();
                _allClientPairs.TryRemove(key, out _);
            }
        }

        RecreateLazy();
    }

    public void SetGroupInfo(GroupInfoDto dto, int serverIndex)
    {
        var key = BuildKey(dto.Group, serverIndex);
        _allGroups[key].Group = dto.Group;
        _allGroups[key].Owner = dto.Owner;
        _allGroups[key].GroupPermissions = dto.GroupPermissions;

        RecreateLazy();
    }

    public void UpdatePairPermissions(UserPermissionsDto dto, int serverIndex)
    {
        var key = BuildKey(dto.User, serverIndex);
        if (!_allClientPairs.TryGetValue(key, out var pair))
        {
            throw new InvalidOperationException("No such pair for " + dto);
        }

        if (pair.UserPair == null)
        {
            throw new InvalidOperationException("No direct pair for " + dto);
        }

        if (pair.UserPair.OtherPermissions.IsPaused() != dto.Permissions.IsPaused())
        {
            var message = new ServerBasedUserKey(dto.User, serverIndex);
            Mediator.Publish(new ClearProfileDataMessage(message));
        }

        pair.UserPair.OtherPermissions = dto.Permissions;

        Logger.LogTrace("Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}",
            pair.UserPair.OtherPermissions.IsPaused(),
            pair.UserPair.OtherPermissions.IsDisableAnimations(),
            pair.UserPair.OtherPermissions.IsDisableSounds(),
            pair.UserPair.OtherPermissions.IsDisableVFX());

        if (!pair.IsPaused)
            pair.ApplyLastReceivedData();

        RecreateLazy();
    }

    public void UpdateSelfPairPermissions(UserPermissionsDto dto, int serverIndex)
    {
        var key = BuildKey(dto.User, serverIndex);
        if (!_allClientPairs.TryGetValue(key, out var pair))
        {
            throw new InvalidOperationException("No such pair for " + dto);
        }

        if (pair.UserPair.OwnPermissions.IsPaused() != dto.Permissions.IsPaused())
        {
            var message = new ServerBasedUserKey(dto.User, serverIndex);
            Mediator.Publish(new ClearProfileDataMessage(message));
        }

        pair.UserPair.OwnPermissions = dto.Permissions;

        Logger.LogTrace("Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}",
            pair.UserPair.OwnPermissions.IsPaused(),
            pair.UserPair.OwnPermissions.IsDisableAnimations(),
            pair.UserPair.OwnPermissions.IsDisableSounds(),
            pair.UserPair.OwnPermissions.IsDisableVFX());

        if (!pair.IsPaused)
            pair.ApplyLastReceivedData();

        RecreateLazy();
    }

    internal void ReceiveUploadStatus(UserDto dto, int serverIndex)
    {
        var key = BuildKey(dto.User, serverIndex);
        if (_allClientPairs.TryGetValue(key, out var existingPair) && existingPair.IsVisible)
        {
            existingPair.SetIsUploading();
        }
    }

    internal void SetGroupPairStatusInfo(GroupPairUserInfoDto dto, int serverIndex)
    {
        var key = BuildKey(dto.Group, serverIndex);
        _allGroups[key].GroupPairUserInfos[dto.UID] = dto.GroupUserInfo;
        RecreateLazy();
    }

    internal void SetGroupPermissions(GroupPermissionDto dto, int serverIndex)
    {
        var key = BuildKey(dto.Group, serverIndex);
        _allGroups[key].GroupPermissions = dto.Permissions;
        RecreateLazy();
    }

    internal void SetGroupStatusInfo(GroupPairUserInfoDto dto, int serverIndex)
    {
        var key = BuildKey(dto.Group, serverIndex);
        _allGroups[key].GroupUserInfo = dto.GroupUserInfo;
        RecreateLazy();
    }

    internal void UpdateGroupPairPermissions(GroupPairUserPermissionDto dto, int serverIndex)
    {
        var key = BuildKey(dto.Group, serverIndex);
        _allGroups[key].GroupUserPermissions = dto.GroupPairPermissions;
        RecreateLazy();
    }

    internal void UpdateIndividualPairStatus(UserIndividualPairStatusDto dto, int serverIndex)
    {
        var key = BuildKey(dto.User, serverIndex);
        if (_allClientPairs.TryGetValue(key, out var pair))
        {
            pair.UserPair.IndividualPairStatus = dto.IndividualPairStatus;
            RecreateLazy();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _dalamudContextMenu.OnMenuOpened -= DalamudContextMenuOnOnOpenGameObjectContextMenu;

        DisposePairs(null);
    }

    private void DalamudContextMenuOnOnOpenGameObjectContextMenu(IMenuOpenedArgs args)
    {
        if (args.MenuType == ContextMenuType.Inventory) return;
        if (!_configurationService.Current.EnableRightClickMenus) return;
        var targetNameProperty = args.Target.GetType().GetProperties().FirstOrDefault(p => string.Equals(p.Name, "TargetName", StringComparison.CurrentCultureIgnoreCase));
        var targetName = targetNameProperty?.GetValue(args.Target)?.ToString() ?? string.Empty;
        var uniquePlayerPairs = _allClientPairs.Where(p=> p.Value.IsVisible && string.Equals(p.Value.PlayerName, targetName, StringComparison.CurrentCultureIgnoreCase))
            .Distinct();

        uniquePlayerPairs.FirstOrDefault().Value.AddContextMenu(args, uniquePlayerPairs);
    }

    private Lazy<List<Pair>> DirectPairsLazy() => new(() => _allClientPairs.Select(k => k.Value)
        .Where(k => k.IndividualPairStatus != IndividualPairStatus.None).ToList());

    private void DisposePairs(int? serverIndex)
    {
        if (serverIndex == null)
        {
            Logger.LogDebug("Disposing all Pairs");
            Parallel.ForEach(_allClientPairs, item =>
            {
                item.Value.MarkOffline(wait: false);
            });
        }
        else
        {
            Logger.LogDebug("Disposing all Pairs for server {ServerIndex}", serverIndex);
            var toDispose = _allClientPairs.Where(item => item.Key.ServerIndex == serverIndex).Select(item => item.Value);
            var toRedraw = _allClientPairs.Where(
                item => item.Value.IsVisible &&
                item.Key.ServerIndex != serverIndex &&
                toDispose.Any(disposePair => disposePair.GetPlayerNameHash().Equals(item.Value.GetPlayerNameHash(), StringComparison.Ordinal)))
                .DistinctBy(item => item.Value.GetPlayerNameHash())
                .Select(item => item.Key)
                .DeepClone();

            Parallel.ForEach(toDispose, disposePair =>
            {
                disposePair.MarkOffline(wait: false);
            });

            Parallel.ForEach(_allClientPairs.Where(item => toRedraw.Contains(item.Key)).Select(item => item.Value), redrawPair =>
            {
                redrawPair.ApplyLastReceivedData(forced: true);
            });
        }
    }

    private Lazy<Dictionary<GroupFullInfoWithServer, List<Pair>>> GroupPairsLazy()
    {
        return new Lazy<Dictionary<GroupFullInfoWithServer, List<Pair>>>(() =>
        {
            Dictionary<GroupFullInfoWithServer, List<Pair>> outDict = [];
            foreach (var group in _allGroups)
            {
                var key = new GroupFullInfoWithServer(group.Key.ServerIndex, group.Value);
                outDict[key] = _allClientPairs.Select(p => p.Value).Where(p =>
                    p.UserPair.Groups.Exists(g => GroupDataComparer.Instance.Equals(group.Key.GroupData, new(g)))).ToList();
            }

            return outDict;
        });
    }

    private Lazy<Dictionary<Pair, List<GroupFullInfoDto>>> PairsWithGroupsLazy()
    {
        return new Lazy<Dictionary<Pair, List<GroupFullInfoDto>>>(() =>
        {
            Dictionary<Pair, List<GroupFullInfoDto>> outDict = [];

            foreach (var pair in _allClientPairs.Select(k => k.Value))
            {
                outDict[pair] = _allGroups.Where(k => pair.UserPair.Groups.Contains(k.Key.GroupData.GID, StringComparer.Ordinal))
                    .Select(k => k.Value).ToList();
            }

            return outDict;
        });
    }

    private void ReapplyPairData()
    {
        foreach (var pair in _allClientPairs.Select(k => k.Value))
        {
            pair.ApplyLastReceivedData(forced: true);
        }
    }

    /// <summary>
    /// Whenever a pair disconnects (either because of a disconnect on the other side or because of a pause), that pair might
    /// still be visible through another connected server.
    /// This happens in scenarios where the the disconnect/pause only happens through one server, but the other server still being available.
    /// In cases like this, more than one pair exists for the same player, because one pair exists per server.
    ///
    /// The PairManager will dispose the pair that just went offline. The other pair, however, is not aware of it. So we
    /// need to figure out if any of these pairs are left, and then redraw them by reapplying last data. 
    /// </summary>
    /// <param name="removedPair">The instance of the pair that got removed</param>
    /// <param name="removedPlayerName">The name of the remvoed pair. Don't remove this, the pair has to be disposed
    /// before we try to redraw other pairs, so the name will not be available anymore!
    /// </param>
    private void RedrawStillVisiblePairs(Pair removedPair, string? removedPlayerName)
    {
        _allClientPairs
            .Where(valuePair => string.Equals(valuePair.Value.PlayerName, removedPlayerName, StringComparison.OrdinalIgnoreCase))
            .Where(valuePair => removedPair != valuePair.Value)
            .Where(valuePair => valuePair.Value.IsVisible)
            .Select(valuePair => valuePair.Value)
            .ToList()
            .ForEach(p => p.ApplyLastReceivedData(true));
    }

    private void RecreateLazy()
    {
        _directPairsInternal = DirectPairsLazy();
        _groupPairsInternal = GroupPairsLazy();
        _pairsWithGroupsInternal = PairsWithGroupsLazy();
        Mediator.Publish(new RefreshUiMessage());
    }

    private ServerBasedUserKey BuildKey(UserData user, int serverIndex)
    {
        return new ServerBasedUserKey(user, serverIndex);
    }

    private ServerBasedGroupKey BuildKey(GroupData group, int serverIndex)
    {
        return new ServerBasedGroupKey(group, serverIndex);
    }
}