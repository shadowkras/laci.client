using LaciSynchroni.Common.Data;
using LaciSynchroni.Common.Dto.Files;
using LaciSynchroni.Common.Routes;
using LaciSynchroni.FileCache;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration;
using LaciSynchroni.UI;
using LaciSynchroni.WebAPI.Files.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace LaciSynchroni.WebAPI.Files;

using ServerIndex = int;

public sealed class FileUploadManager : DisposableMediatorSubscriberBase
{
    private readonly FileCacheManager _fileDbManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly ServerConfigurationManager _serverManager;
    private readonly Dictionary<string, DateTime> _verifiedUploadedHashes = new(StringComparer.Ordinal);
    /// <summary>
    /// One Cancellation token per server, since we can concurrently upload to each server connected.
    /// </summary>
    private readonly ConcurrentDictionary<ServerIndex, CancellationTokenSource> _cancellationTokens = new();
    /// <summary>
    /// Per-server upload tracking to prevent race conditions during concurrent uploads.
    /// </summary>
    private readonly ConcurrentDictionary<ServerIndex, List<FileTransfer>> _currentUploads = new();

    public FileUploadManager(ILogger<FileUploadManager> logger, SyncMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileDbManager,
        ServerConfigurationManager serverManager) : base(logger, mediator)
    {
        _orchestrator = orchestrator;
        _fileDbManager = fileDbManager;
        _serverManager = serverManager;

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            ResetForServer(msg.ServerIndex);
        });
    }

    /// <summary>
    /// Returns all uploads across all servers. Thread-safe aggregate view.
    /// </summary>
    public List<FileTransfer> CurrentUploads => _currentUploads.Values.SelectMany(x => x).ToList();

    /// <summary>
    /// Gets the upload list for a specific server. Returns a new empty list if none exists.
    /// </summary>
    private List<FileTransfer> GetServerUploads(ServerIndex serverIndex)
    {
        return _currentUploads.GetOrAdd(serverIndex, _ => []);
    }

    public async Task DeleteAllFiles(int serverIndex)
    {
        var uri = RequireUriForServer(serverIndex);

        await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Post, FilesRoutes.ServerFilesDeleteAllFullPath(uri)).ConfigureAwait(false);
    }

    public async Task<List<string>> UploadFiles(int serverIndex, string serverName, List<string> hashesToUpload, IProgress<string> progress, CancellationToken ct)
    {

        Logger.LogDebug("Trying to upload files to {ServerName}", serverName);
        var filesPresentLocally = hashesToUpload.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);
        var locallyMissingFiles = hashesToUpload.Except(filesPresentLocally, StringComparer.Ordinal).ToList();
        if (locallyMissingFiles.Any())
        {
            return locallyMissingFiles;
        }

        progress.Report($"Starting upload to {serverName} for {filesPresentLocally.Count} files");

        var filesToUpload = await FilesSend(serverIndex, [.. filesPresentLocally], [], ct).ConfigureAwait(false);

        if (filesToUpload.Exists(f => f.IsForbidden))
        {
            return [.. filesToUpload.Where(f => f.IsForbidden).Select(f => f.Hash)];
        }

        Task uploadTask = Task.CompletedTask;
        int i = 1;
        foreach (var file in filesToUpload)
        {
            progress.Report($"Uploading file {i++}/{filesToUpload.Count} to {serverName}. Please wait until the upload is completed.");
            Logger.LogDebug("[{Hash}] Compressing", file);
            var data = await _fileDbManager.GetCompressedFileData(file.Hash, ct).ConfigureAwait(false);
            Logger.LogDebug("[{Hash}] Starting upload on {ServerName} for {FilePath}", data.Item1, serverName, _fileDbManager.GetFileCacheByHash(data.Item1)!.ResolvedFilepath);
            await uploadTask.ConfigureAwait(false);
            uploadTask = UploadFile(serverIndex, serverName, data.Item2, file.Hash, false, ct);
            ct.ThrowIfCancellationRequested();
        }

        await uploadTask.ConfigureAwait(false);

        return [];
    }

    public async Task<CharacterData> UploadFiles(ServerIndex serverIndex, string serverName, CharacterData data, List<UserData> visiblePlayers)
    {
        CancelUpload(serverIndex);

        var tokenSource = new CancellationTokenSource();
        if (!_cancellationTokens.TryAdd(serverIndex, tokenSource))
        {
            Logger.LogError("[{ServerIndex} Failed to add cancellation token to {ServerName}, token already present.", serverIndex, serverName);
        }
        var uploadToken = tokenSource.Token;
        Logger.LogDebug("[{Hash}] Sending Character data to service {Url}", data.DataHash.Value, _serverManager.GetServerByIndex(serverIndex).ServerUri);

        HashSet<string> unverifiedUploads = GetUnverifiedFiles(data);
        if (unverifiedUploads.Any())
        {
            await UploadUnverifiedFiles(serverIndex, serverName, unverifiedUploads, visiblePlayers, uploadToken).ConfigureAwait(false);
            Logger.LogInformation("[{Hash}] Upload complete for {ServerName}", data.DataHash.Value, serverName);
        }

        foreach (var kvp in data.FileReplacements)
        {
            data.FileReplacements[kvp.Key].RemoveAll(i => _orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, i.Hash, StringComparison.OrdinalIgnoreCase)));
        }

        return data;
    }

    private async Task<List<UploadFileDto>> FilesSend(int serverIndex, List<string> hashes, List<string> uids, CancellationToken ct)
    {
        var uri = RequireUriForServer(serverIndex);
        FilesSendDto filesSendDto = new()
        {
            FileHashes = hashes,
            UIDs = uids
        };
        var response = await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Post, FilesRoutes.ServerFilesFilesSendFullPath(uri), filesSendDto, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<UploadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private HashSet<string> GetUnverifiedFiles(CharacterData data)
    {
        HashSet<string> unverifiedUploadHashes = new(StringComparer.Ordinal);
        foreach (var item in data.FileReplacements.SelectMany(c => c.Value.Where(f => string.IsNullOrEmpty(f.FileSwapPath)).Select(v => v.Hash).Distinct(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList())
        {
            if (!_verifiedUploadedHashes.TryGetValue(item, out var verifiedTime))
            {
                verifiedTime = DateTime.MinValue;
            }

            if (verifiedTime < DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(10)))
            {
                Logger.LogTrace("Verifying {Item}, last verified: {Date}", item, verifiedTime);
                unverifiedUploadHashes.Add(item);
            }
        }

        return unverifiedUploadHashes;
    }

    private async Task UploadFile(int serverIndex, string serverName, byte[] compressedFile, string fileHash, bool postProgress, CancellationToken uploadToken)
    {
        Logger.LogInformation("[{hash}] Uploading {size}", fileHash, UiSharedService.ByteToString(compressedFile.Length));

        var serverInfo = _serverManager.GetServerByIndex(serverIndex);

        if (uploadToken.IsCancellationRequested) return;

        try
        {
            await UploadFileStream(serverIndex, compressedFile, fileHash, serverInfo.UseAlternativeFileUpload, postProgress, uploadToken).ConfigureAwait(false);
            _verifiedUploadedHashes[fileHash] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            if (!serverInfo.UseAlternativeFileUpload && ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "[{Hash}] Error during file upload, trying alternative file upload", fileHash);
                await UploadFileStream(serverIndex, compressedFile, fileHash, munged: true, postProgress, uploadToken).ConfigureAwait(false);
            }
            else
            {
                Logger.LogWarning(ex, "[{Hash}] File upload cancelled", fileHash);
            }
        }
    }

    private async Task UploadFileStream(int serverIndex, byte[] compressedFile, string fileHash, bool munged, bool postProgress, CancellationToken uploadToken)
    {
        var uri = RequireUriForServer(serverIndex);
        if (munged)
        {
            FileDownloadManager.MungeBuffer(compressedFile.AsSpan());
        }

        using var ms = new MemoryStream(compressedFile);

        Progress<UploadProgress>? prog = !postProgress ? null : new((prog) =>
        {
            try
            {
                CurrentUploads.Single(f => string.Equals(f.Hash, fileHash, StringComparison.Ordinal) && f.ServerIndex == serverIndex).Transferred = prog.Uploaded;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[{Hash}] Could not set upload progress", fileHash);
            }
        });

        var streamContent = new ProgressableStreamContent(ms, prog);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage response;
        if (!munged)
            response = await _orchestrator.SendRequestStreamAsync(serverIndex, HttpMethod.Post, FilesRoutes.ServerFilesUploadFullPath(uri, fileHash), streamContent, uploadToken).ConfigureAwait(false);
        else
            response = await _orchestrator.SendRequestStreamAsync(serverIndex, HttpMethod.Post, FilesRoutes.ServerFilesUploadMunged(uri, fileHash), streamContent, uploadToken).ConfigureAwait(false);
        Logger.LogDebug("[{Hash}] Upload Status: {Status}", fileHash, response.StatusCode);
    }

    private async Task UploadUnverifiedFiles(int serverIndex, string serverName, HashSet<string> unverifiedUploadHashes, List<UserData> visiblePlayers, CancellationToken uploadToken)
    {
        unverifiedUploadHashes = unverifiedUploadHashes
            .Where(h => _fileDbManager.GetFileCacheByHash(h) != null)
            .ToHashSet(StringComparer.Ordinal);

        Logger.LogDebug("Verifying {Count} files", unverifiedUploadHashes.Count);

        var filesToUpload = await FilesSend(serverIndex, [.. unverifiedUploadHashes], visiblePlayers.Select(p => p.UID).ToList(), uploadToken).ConfigureAwait(false);

        var serverUploads = GetServerUploads(serverIndex);
        foreach (var file in filesToUpload.Where(f => !f.IsForbidden).DistinctBy(f => f.Hash))
        {
            try
            {
                serverUploads.Add(new UploadFileTransfer(file, serverIndex)
                {
                    Total = new FileInfo(_fileDbManager.GetFileCacheByHash(file.Hash)!.ResolvedFilepath).Length,
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Tried to request file {Hash} but file was not present", file.Hash);
            }
        }

        foreach (var file in filesToUpload.Where(c => c.IsForbidden))
        {
            if (_orchestrator.ForbiddenTransfers.TrueForAll(f => !string.Equals(f.Hash, file.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new UploadFileTransfer(file, serverIndex)
                {
                    LocalFile = _fileDbManager.GetFileCacheByHash(file.Hash)?.ResolvedFilepath ?? string.Empty,
                });
            }

            _verifiedUploadedHashes[file.Hash] = DateTime.UtcNow;
        }

        var totalSize = CurrentUploads.Where(c => c.ServerIndex == serverIndex).Sum(c => c.Total);
        Logger.LogDebug("Compressing and uploading files");
        Task uploadTask = Task.CompletedTask;
        foreach (var file in CurrentUploads.Where(f => f.ServerIndex == serverIndex && f.CanBeTransferred && !f.IsTransferred).ToList())
        {
            Logger.LogDebug("[{hash}] Compressing", file);
            var data = await _fileDbManager.GetCompressedFileData(file.Hash, uploadToken).ConfigureAwait(false);
            CurrentUploads.Single(e => string.Equals(e.Hash, data.Item1, StringComparison.Ordinal) && e.ServerIndex == serverIndex).Total = data.Item2.Length;
            Logger.LogDebug("[{hash}] Starting upload for {filePath}", data.Item1, _fileDbManager.GetFileCacheByHash(data.Item1)!.ResolvedFilepath);
            await uploadTask.ConfigureAwait(false);
            uploadTask = UploadFile(serverIndex, data.Item2, file.Hash, true, uploadToken);
            uploadToken.ThrowIfCancellationRequested();
        }

        var uploadsForThisServer = CurrentUploads.Where(c => c.ServerIndex == serverIndex).ToList();
        if (uploadsForThisServer.Any())
        {
            _verifiedUploadedHashes[file] = DateTime.UtcNow;
        }

            var compressedSize = uploadsForThisServer.Sum(c => c.Total);
            Logger.LogDebug("Upload complete, compressed {size} to {compressed}", UiSharedService.ByteToString(totalSize), UiSharedService.ByteToString(compressedSize));
        }

        foreach (var file in unverifiedUploadHashes.Where(c => !CurrentUploads.Exists(u => string.Equals(u.Hash, c, StringComparison.Ordinal) && u.ServerIndex == serverIndex)))
        {
            _verifiedUploadedHashes[file] = DateTime.UtcNow;
        }

        CurrentUploads.RemoveAll(transfer => transfer.ServerIndex == serverIndex);
    }

    private void CancelUpload(ServerIndex serverIndex)
    {
        CancelUploadsToServer(serverIndex);
    }

    private void CancelUploadsToServer(ServerIndex serverIndex)
    {
        if (_cancellationTokens.TryRemove(serverIndex, out var token))
        {
            token.Cancel();
            token.Dispose();
        }
        _currentUploads.TryRemove(serverIndex, out _);
    }

    private Uri RequireUriForServer(int serverIndex)
    {
        var uri = _orchestrator.GetFileCdnUri(serverIndex);
        if (uri == null) throw new InvalidOperationException("FileTransferManager is not initialized");
        return uri;
    }

    private void ResetForServer(ServerIndex serverIndex)
    {
        CancelUploadsToServer(serverIndex);
        _verifiedUploadedHashes.Clear();
    }

    private void Reset()
    {
        _cancellationTokens.Values.ToList().ForEach(c =>
        {
            c.Cancel();
            c.Dispose();
        });
        _cancellationTokens.Clear();
        _currentUploads.Clear();
        _verifiedUploadedHashes.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Reset();
    }
}