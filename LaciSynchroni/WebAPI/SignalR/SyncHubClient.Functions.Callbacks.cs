using LaciSynchroni.Common.Data;
using LaciSynchroni.Common.Data.Enum;
using LaciSynchroni.Common.Dto;
using LaciSynchroni.Common.Dto.CharaData;
using LaciSynchroni.Common.Dto.Group;
using LaciSynchroni.Common.Dto.User;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.SyncConfiguration.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.WebAPI;

public partial class SyncHubClient
{
    public Task Client_DownloadReady(Guid requestId)
    {
        Logger.LogDebug("Server sent {requestId} ready", requestId);
        Mediator.Publish(new DownloadReadyMessage(requestId));
        return Task.CompletedTask;
    }

    public Task Client_GroupChangePermissions(GroupPermissionDto groupPermission)
    {
        Logger.LogTrace("Client_GroupChangePermissions: {perm}", groupPermission);
        ExecuteSafely(() => _pairManager.SetGroupPermissions(groupPermission, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_GroupChangeUserPairPermissions(GroupPairUserPermissionDto dto)
    {
        Logger.LogDebug("Client_GroupChangeUserPairPermissions: {dto}", dto);
        ExecuteSafely(() => _pairManager.UpdateGroupPairPermissions(dto, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_GroupDelete(GroupDto groupDto)
    {
        Logger.LogTrace("Client_GroupDelete: {dto}", groupDto);
        ExecuteSafely(() => _pairManager.RemoveGroup(groupDto.Group, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_GroupPairChangeUserInfo(GroupPairUserInfoDto userInfo)
    {
        Logger.LogTrace("Client_GroupPairChangeUserInfo: {dto}", userInfo);
        ExecuteSafely(() =>
        {
            if (string.Equals(userInfo.UID, UID, StringComparison.Ordinal))
                _pairManager.SetGroupStatusInfo(userInfo, ServerIndex);
            else _pairManager.SetGroupPairStatusInfo(userInfo, ServerIndex);
        });
        return Task.CompletedTask;
    }

    public Task Client_GroupPairJoined(GroupPairFullInfoDto groupPairInfoDto)
    {
        Logger.LogTrace("Client_GroupPairJoined: {dto}", groupPairInfoDto);
        ExecuteSafely(() => _pairManager.AddGroupPair(groupPairInfoDto, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_GroupPairLeft(GroupPairDto groupPairDto)
    {
        Logger.LogTrace("Client_GroupPairLeft: {dto}", groupPairDto);
        ExecuteSafely(() => _pairManager.RemoveGroupPair(groupPairDto, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_GroupSendFullInfo(GroupFullInfoDto groupInfo)
    {
        Logger.LogTrace("Client_GroupSendFullInfo: {dto}", groupInfo);
        ExecuteSafely(() => _pairManager.AddGroup(groupInfo, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_GroupSendInfo(GroupInfoDto groupInfo)
    {
        Logger.LogTrace("Client_GroupSendInfo: {dto}", groupInfo);
        ExecuteSafely(() => _pairManager.SetGroupInfo(groupInfo, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_ReceiveServerMessage(MessageSeverity messageSeverity, string message)
    {
        var serverName = ServerToUse.ServerName;
        switch (messageSeverity)
        {
            case MessageSeverity.Error:
                Mediator.Publish(new NotificationMessage("Warning from " + serverName, $"({serverName}) {message}",
                    NotificationType.Error, TimeSpan.FromSeconds(7.5)));
                break;

            case MessageSeverity.Warning:
                Mediator.Publish(new NotificationMessage("Warning from " + serverName, $"({serverName}) {message}",
                    NotificationType.Warning, TimeSpan.FromSeconds(7.5)));
                break;

            case MessageSeverity.Information:
                if (_doNotNotifyOnNextInfo)
                {
                    _doNotNotifyOnNextInfo = false;
                    break;
                }

                Mediator.Publish(new NotificationMessage("Info from " + serverName, $"({serverName}) {message}",
                    NotificationType.Info, TimeSpan.FromSeconds(5)));
                break;
        }

        return Task.CompletedTask;
    }

    public Task Client_ReceivePairingMessage(UserDto dto)
    {
        Logger.LogDebug("Got a request to pair from {Uid}", dto.User.UID);
        var pair = _pairManager.GetPairByUID(ServerIndex, dto.User.UID);
        if (pair == null) 
            return Task.CompletedTask;

        var player = string.IsNullOrEmpty(pair.PlayerName) ? dto.User.AliasOrUID : pair.PlayerName;
        Logger.LogDebug("Got a request to pair from {Uid} mapping to {Player}.", dto.User.UID, player);
        
        if (_serverConfigurationManager.GetServerByIndex(ServerIndex).ShowPairingRequestNotification)
        {
            _pairManager.AddUserPairRequest(ServerIndex, dto.User);
            Mediator.Publish(new NotificationMessage("Incoming direct pair request.",
                $"Player {player} would like to pair. Their UID is {dto.User.AliasOrUID}.", NotificationType.Info, TimeSpan.FromSeconds(15)));
        }
        return Task.CompletedTask;
    }

    public Task Client_UpdateSystemInfo(SystemInfoDto systemInfo)
    {
        SystemInfoDto = systemInfo;
        return Task.CompletedTask;
    }

    public Task Client_UpdateUserIndividualPairStatusDto(UserIndividualPairStatusDto dto)
    {
        Logger.LogDebug("Client_UpdateUserIndividualPairStatusDto: {dto}", dto);
        ExecuteSafely(() => _pairManager.UpdateIndividualPairStatus(dto, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_UserAddClientPair(UserPairDto dto)
    {
        Logger.LogDebug("Client_UserAddClientPair: {dto}", dto);
        ExecuteSafely(() => _pairManager.AddUserPair(dto, ServerIndex, addToLastAddedUser: true));
        return Task.CompletedTask;
    }

    public Task Client_UserReceiveCharacterData(OnlineUserCharaDataDto dataDto)
    {
        Logger.LogTrace("Client_UserReceiveCharacterData: {user}", dataDto.User);
        ExecuteSafely(() => _pairManager.ReceiveCharaData(dataDto, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_UserReceiveUploadStatus(UserDto dto)
    {
        Logger.LogTrace("Client_UserReceiveUploadStatus: {dto}", dto);
        ExecuteSafely(() => _pairManager.ReceiveUploadStatus(dto, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_UserRemoveClientPair(UserDto dto)
    {
        Logger.LogDebug("Client_UserRemoveClientPair: {dto}", dto);
        ExecuteSafely(() => _pairManager.RemoveUserPair(dto, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_UserSendOffline(UserDto dto)
    {
        Logger.LogDebug("Client_UserSendOffline: {dto}", dto);
        ExecuteSafely(() => _pairManager.MarkPairOffline(dto.User, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_UserSendOnline(OnlineUserIdentDto dto)
    {
        Logger.LogDebug("Client_UserSendOnline: {dto}", dto);
        ExecuteSafely(() => _pairManager.MarkPairOnline(dto, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_UserUpdateDefaultPermissions(DefaultPermissionsDto dto)
    {
        Logger.LogDebug("Client_UserUpdateDefaultPermissions: {dto}", dto);
        ConnectionDto!.DefaultPreferredPermissions = dto;
        return Task.CompletedTask;
    }

    public Task Client_UserUpdateOtherPairPermissions(UserPermissionsDto dto)
    {
        Logger.LogDebug("Client_UserUpdateOtherPairPermissions: {dto}", dto);
        ExecuteSafely(() => _pairManager.UpdatePairPermissions(dto, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_UserUpdateProfile(UserDto dto)
    {
        Logger.LogDebug("Client_UserUpdateProfile: {dto}", dto);
        var messageContent = new ServerBasedUserKey(dto.User, ServerIndex);
        ExecuteSafely(() => Mediator.Publish(new ClearProfileDataMessage(messageContent)));
        return Task.CompletedTask;
    }

    public Task Client_UserUpdateSelfPairPermissions(UserPermissionsDto dto)
    {
        Logger.LogDebug("Client_UserUpdateSelfPairPermissions: {dto}", dto);
        ExecuteSafely(() => _pairManager.UpdateSelfPairPermissions(dto, ServerIndex));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyJoin(UserData userData)
    {
        Logger.LogDebug("Client_GposeLobbyJoin: {dto}", userData);
        ExecuteSafely(() => Mediator.Publish(new GposeLobbyUserJoin(ServerIndex, userData)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyLeave(UserData userData)
    {
        Logger.LogDebug("Client_GposeLobbyLeave: {dto}", userData);
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyUserLeave(userData)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyPushCharacterData(CharaDataDownloadDto charaDownloadDto)
    {
        Logger.LogDebug("Client_GposeLobbyPushCharacterData: {dto}", charaDownloadDto.Uploader);
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyReceiveCharaData(ServerIndex, charaDownloadDto)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyPushPoseData(UserData userData, PoseData poseData)
    {
        Logger.LogDebug("Client_GposeLobbyPushPoseData: {dto}", userData);
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyReceivePoseData(userData, poseData)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyPushWorldData(UserData userData, WorldData worldData)
    {
        //Logger.LogDebug("Client_GposeLobbyPushWorldData: {dto}", userData);
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyReceiveWorldData(userData, worldData)));
        return Task.CompletedTask;
    }

    public void OnDownloadReady(Action<Guid> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_DownloadReady), act);
    }

    public void OnGroupChangePermissions(Action<GroupPermissionDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GroupChangePermissions), act);
    }

    public void OnGroupChangeUserPairPermissions(Action<GroupPairUserPermissionDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GroupChangeUserPairPermissions), act);
    }

    public void OnGroupDelete(Action<GroupDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GroupDelete), act);
    }

    public void OnGroupPairChangeUserInfo(Action<GroupPairUserInfoDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GroupPairChangeUserInfo), act);
    }

    public void OnGroupPairJoined(Action<GroupPairFullInfoDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GroupPairJoined), act);
    }

    public void OnGroupPairLeft(Action<GroupPairDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GroupPairLeft), act);
    }

    public void OnGroupSendFullInfo(Action<GroupFullInfoDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GroupSendFullInfo), act);
    }

    public void OnGroupSendInfo(Action<GroupInfoDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GroupSendInfo), act);
    }

    public void OnReceiveServerMessage(Action<MessageSeverity, string> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_ReceiveServerMessage), act);
    }

    public void OnReceivePairingMessage(Action<UserDto> act)
    {
        Logger.LogDebug("ReceievedPairingMessage");
        if (_initialized) return;
        _connection!.On(nameof(Client_ReceivePairingMessage), act);
    }

    public void OnUpdateSystemInfo(Action<SystemInfoDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UpdateSystemInfo), act);
    }

    public void OnUpdateUserIndividualPairStatusDto(Action<UserIndividualPairStatusDto> action)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UpdateUserIndividualPairStatusDto), action);
    }

    public void OnUserAddClientPair(Action<UserPairDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UserAddClientPair), act);
    }

    public void OnUserDefaultPermissionUpdate(Action<DefaultPermissionsDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UserUpdateDefaultPermissions), act);
    }

    public void OnUserReceiveCharacterData(Action<OnlineUserCharaDataDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UserReceiveCharacterData), act);
    }

    public void OnUserReceiveUploadStatus(Action<UserDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UserReceiveUploadStatus), act);
    }

    public void OnUserRemoveClientPair(Action<UserDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UserRemoveClientPair), act);
    }

    public void OnUserSendOffline(Action<UserDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UserSendOffline), act);
    }

    public void OnUserSendOnline(Action<OnlineUserIdentDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UserSendOnline), act);
    }

    public void OnUserUpdateOtherPairPermissions(Action<UserPermissionsDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UserUpdateOtherPairPermissions), act);
    }

    public void OnUserUpdateProfile(Action<UserDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UserUpdateProfile), act);
    }

    public void OnUserUpdateSelfPairPermissions(Action<UserPermissionsDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_UserUpdateSelfPairPermissions), act);
    }

    public void OnGposeLobbyJoin(Action<UserData> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GposeLobbyJoin), act);
    }

    public void OnGposeLobbyLeave(Action<UserData> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GposeLobbyLeave), act);
    }

    public void OnGposeLobbyPushCharacterData(Action<CharaDataDownloadDto> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GposeLobbyPushCharacterData), act);
    }

    public void OnGposeLobbyPushPoseData(Action<UserData, PoseData> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GposeLobbyPushPoseData), act);
    }

    public void OnGposeLobbyPushWorldData(Action<UserData, WorldData> act)
    {
        if (_initialized) return;
        _connection!.On(nameof(Client_GposeLobbyPushWorldData), act);
    }

    private void ExecuteSafely(Action act)
    {
        try
        {
            act();
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Error on executing safely");
        }
    }
}