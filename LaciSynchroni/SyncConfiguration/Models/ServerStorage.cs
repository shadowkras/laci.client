﻿using Microsoft.AspNetCore.Http.Connections;

namespace LaciSynchroni.SyncConfiguration.Models;

[Serializable]
public class ServerStorage
{
    public List<Authentication> Authentications { get; set; } = [];
    public bool FullPause { get; set; } = false;
    public Dictionary<int, SecretKey> SecretKeys { get; set; } = [];
    public string ServerName { get; set; } = string.Empty;
    public string ServerUri { get; set; } = string.Empty;
    public string ServerHubUri { get; set; } = string.Empty;
    public bool UseAdvancedUris { get; set; } = false;
    public bool EnableObfuscationDownloadedFiles { get; set; } = true;
    public bool UseAlternativeFileUpload { get; set; } = false;
    public bool ShowPairingRequestNotification { get; set; } = false;
    public bool BypassVersionCheck { get; set; } = false;
    public bool UseOAuth2 { get; set; } = false;
    public string? OAuthToken { get; set; } = null;
    public HttpTransportType HttpTransportType { get; set; } = HttpTransportType.WebSockets;
    public bool ForceWebSockets { get; set; } = false;
}