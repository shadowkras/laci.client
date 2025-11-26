using Dalamud.Utility;
using K4os.Compression.LZ4.Legacy;
using LaciSynchroni.Common.Data;
using LaciSynchroni.Common.Dto.Files;
using LaciSynchroni.Common.Routes;
using LaciSynchroni.FileCache;
using LaciSynchroni.PlayerData.Handlers;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.WebAPI.Files.Models;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace LaciSynchroni.WebAPI.Files;

public partial class FileDownloadManager : DisposableMediatorSubscriberBase
{
    private readonly Dictionary<string, FileDownloadStatus> _downloadStatus;
    private readonly FileCompactor _fileCompactor;
    private readonly FileCacheManager _fileDbManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly List<ThrottledStream> _activeDownloadStreams;
    private readonly ServerConfigurationManager _serverManager;

    public FileDownloadManager(ILogger<FileDownloadManager> logger, SyncMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor,
        ServerConfigurationManager serverManager) : base(logger, mediator)
    {
        _downloadStatus = new Dictionary<string, FileDownloadStatus>(StringComparer.Ordinal);
        _orchestrator = orchestrator;
        _fileDbManager = fileCacheManager;
        _fileCompactor = fileCompactor;
        _serverManager = serverManager;
        _activeDownloadStreams = [];

        Mediator.Subscribe<DownloadLimitChangedMessage>(this, (msg) =>
        {
            if (!_activeDownloadStreams.Any()) return;
            var newLimit = _orchestrator.DownloadLimitPerSlot();
            Logger.LogTrace("Setting new Download Speed Limit to {NewLimit}", newLimit);
            foreach (var stream in _activeDownloadStreams)
            {
                stream.BandwidthLimit = newLimit;
            }
        });
    }

    public List<DownloadFileTransfer> CurrentDownloads { get; private set; } = [];

    public List<FileTransfer> ForbiddenTransfers => _orchestrator.ForbiddenTransfers;

    public bool IsDownloading => !CurrentDownloads.Any();

    /// <summary>
    /// XORs each byte in the buffer with 42 (small obfuscation).
    /// </summary>
    /// <param name="buffer"></param>
    public static void MungeBuffer(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; ++i)
        {
            buffer[i] ^= 42;
        }
    }

    public void ClearDownload()
    {
        CurrentDownloads.Clear();
        _downloadStatus.Clear();
    }

    public async Task DownloadFiles(int serverIndex, GameObjectHandler gameObject, List<FileReplacementData> fileReplacementDto, CancellationToken ct)
    {
        Mediator.Publish(new HaltScanMessage(nameof(DownloadFiles)));
        try
        {
            var serverName = _serverManager.GetServerNameByIndex(serverIndex);
            Logger.LogDebug("Downloading files from {ServerName}", serverName);
            var downloadFilesTask = DownloadFilesInternal(serverIndex, gameObject, fileReplacementDto, ct);
            var directDownloadFilesTask = DirectDownloadFilesInternal(serverIndex, gameObject, fileReplacementDto, ct);
            await Task.WhenAll(downloadFilesTask, directDownloadFilesTask).ConfigureAwait(false);
        }
        catch
        {
            ClearDownload();
        }
        finally
        {
            Mediator.Publish(new DownloadFinishedMessage(gameObject));
            Mediator.Publish(new ResumeScanMessage(nameof(DownloadFiles)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        ClearDownload();
        foreach (var stream in _activeDownloadStreams.ToList())
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // do nothing
                //
            }
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Munges a single byte, throws EndOfStreamException if input is -1 (EOF).
    /// </summary>
    /// <param name="byteOrEof"></param>
    /// <returns></returns>
    /// <exception cref="EndOfStreamException"></exception>
    private static byte MungeByte(int byteOrEof)
    {
        if (byteOrEof == -1)
        {
            throw new EndOfStreamException();
        }

        return (byte)(byteOrEof ^ 42);
    }

    private int GetTimeZoneUtcOffsetMinutes()
    {
        int result = LongitudinalRegion.FromLocalSystemTimeZone().UtcOffsetMinutes;
        return result;
    }

    /// <summary>
    /// Reads a block file header from the given filestream, optionally munging the bytes.
    /// </summary>
    private static (string fileHash, long fileLengthBytes) ReadBlockFileHeader(FileStream fileBlockStream, bool munge)
    {
        List<char> hashName = [];
        List<char> fileLength = [];
        var separator = munge ? (char)MungeByte(fileBlockStream.ReadByte()) : (char)(fileBlockStream.ReadByte());
        if (separator != '#') throw new InvalidDataException("Data is invalid, first char is not #");

        bool readHash = false;
        while (true)
        {
            int readByte = fileBlockStream.ReadByte();
            if (readByte == -1)
                throw new EndOfStreamException();

            var readChar = munge ? (char)MungeByte(readByte) : (char)(readByte);
            if (readChar == ':')
            {
                readHash = true;
                continue;
            }
            if (readChar == '#') break;
            if (!readHash) hashName.Add(readChar);
            else fileLength.Add(readChar);
        }
        return (string.Join("", hashName), long.Parse(string.Join("", fileLength)));
    }

    /// <summary>
    /// Downloads a file using HttpClient, with optional munging of the data.
    /// </summary>
    private async Task DownloadFileHttpClient(int serverIndex, string downloadGroup, Guid requestId, List<DownloadFileTransfer> fileTransfer, string tempPath, IProgress<long> progress, bool munge, CancellationToken ct)
    {
        Logger.LogDebug("GUID {RequestId} on server {Uri} for files {Files}", requestId, fileTransfer[0].DownloadUri, string.Join(", ", fileTransfer.Select(c => c.Hash).ToList()));

        await WaitForDownloadReady(serverIndex, fileTransfer, requestId, ct).ConfigureAwait(false);

        _downloadStatus[downloadGroup].DownloadStatus = DownloadStatus.Downloading;

        HttpResponseMessage response = null!;
        var requestUrl = FilesRoutes.CacheGetFullPath(fileTransfer[0].DownloadUri, requestId);

        Logger.LogDebug("Downloading {RequestUrl} for request {Id}", requestUrl, requestId);
        try
        {
            response = await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, requestUrl, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Error during download of {RequestUrl}, HttpStatusCode: {Code}", requestUrl, ex.StatusCode);
            if (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                throw new InvalidDataException($"Http error {ex.StatusCode} (cancelled: {ct.IsCancellationRequested}): {requestUrl}", ex);
            }
        }

        ThrottledStream? stream = null;
        try
        {
            var fileStream = File.Create(tempPath);
            await using (fileStream.ConfigureAwait(false))
            {
                var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 65536 : 8196;
                var buffer = new byte[bufferSize];

                var bytesRead = 0;
                var limit = _orchestrator.DownloadLimitPerSlot();
                Logger.LogTrace("Starting Download of {Id} with a speed limit of {Limit} to {TempPath}", requestId, limit, tempPath);
                stream = new ThrottledStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit);
                _activeDownloadStreams.Add(stream);
                while ((bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    // Munge the buffer if needed
                    if (munge)
                        MungeBuffer(buffer.AsSpan(0, bytesRead));

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);

                    progress.Report(bytesRead);
                }

                Logger.LogDebug("{RequestUrl} downloaded to {TempPath}", requestUrl, tempPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            try
            {
                if (!tempPath.IsNullOrEmpty())
                    File.Delete(tempPath);
            }
            catch
            {
                // ignore if file deletion fails
            }
            throw;
        }
        finally
        {
            if (stream != null)
            {
                _activeDownloadStreams.Remove(stream);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<List<DownloadFileTransfer>> InitiateDownloadList(int serverIndex, GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        Logger.LogDebug("Download start: {Id}", gameObjectHandler.Name);

        List<DownloadFileDto> downloadFileInfoFromService =
        [
            .. await FilesGetSizes(serverIndex, fileReplacement.Select(f => f.Hash).Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false),
        ];

        Logger.LogDebug("Files with size 0 or less: {Files}", string.Join(", ", downloadFileInfoFromService.Where(f => f.Size <= 0).Select(f => f.Hash)));

        foreach (var dto in downloadFileInfoFromService.Where(c => c.IsForbidden))
        {
            if (!_orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, dto.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new DownloadFileTransfer(dto, serverIndex));
            }
        }

        CurrentDownloads = downloadFileInfoFromService.Distinct().Select(d => new DownloadFileTransfer(d, serverIndex))
            .Where(d => d.CanBeTransferred).ToList();

        return CurrentDownloads;
    }

    private async Task DownloadFilesInternal(int serverIndex, GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        var enableFileObfuscation = _serverManager.GetServerByIndex(serverIndex)?.EnableObfuscationDownloadedFiles ?? false;
        var downloadGroups = CurrentDownloads.Where(p=> !p.IsDirectDownload).GroupBy(f => f.DownloadUri.Host + ":" + f.DownloadUri.Port, StringComparer.Ordinal);

        if (!downloadGroups.Any())
            return;

        foreach (var downloadGroup in downloadGroups)
        {
            _downloadStatus[downloadGroup.Key] = new FileDownloadStatus()
            {
                DownloadStatus = DownloadStatus.Initializing,
                TotalBytes = downloadGroup.Sum(c => c.Total),
                TotalFiles = 1,
                TransferredBytes = 0,
                TransferredFiles = 0
            };
        }

        Mediator.Publish(new DownloadStartedMessage(gameObjectHandler, _downloadStatus));

        // limit concurrency to number of groups (same as your MaxDegreeOfParallelism)
        using var semaphore = new SemaphoreSlim(downloadGroups.Count());

        var tasks = downloadGroups.Select(async fileGroup =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                // let server predownload files
                var requestIdResponse = await _orchestrator.SendRequestAsync(
                    serverIndex,
                    HttpMethod.Post,
                    FilesRoutes.RequestEnqueueFullPath(fileGroup.First().DownloadUri),
                    fileGroup.Select(c => c.Hash),
                    ct).ConfigureAwait(false);

                Logger.LogDebug("Sent request for {N} files on server {Uri} with result {Result}",
                    fileGroup.Count(),
                    fileGroup.First().DownloadUri,
                    await requestIdResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

                Guid requestId = Guid.Parse((await requestIdResponse.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim('"'));
                Logger.LogDebug("GUID {RequestId} for {N} files on server {Uri}", requestId, fileGroup.Count(), fileGroup.First().DownloadUri);

                var blockFile = _fileDbManager.GetCacheFilePath(requestId.ToString("N"), "blk");
                FileInfo fi = new(blockFile);

                try
                {
                    _downloadStatus[fileGroup.Key].DownloadStatus = DownloadStatus.WaitingForSlot;
                    await _orchestrator.WaitForDownloadSlotAsync(ct).ConfigureAwait(false);
                    _downloadStatus[fileGroup.Key].DownloadStatus = DownloadStatus.WaitingForQueue;

                    Progress<long> progress = new((bytesDownloaded) =>
                    {
                        try
                        {
                            if (!_downloadStatus.TryGetValue(fileGroup.Key, out FileDownloadStatus? value)) return;
                            value.TransferredBytes += bytesDownloaded;
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Could not set download progress");
                        }
                    });

                    await DownloadFileHttpClient(
                        serverIndex,
                        fileGroup.Key,
                        requestId,
                        [.. fileGroup],
                        blockFile,
                        progress,
                        enableFileObfuscation,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogDebug("{DlName}: Detected cancellation of download, partially extracting files for {Id}", fi.Name, gameObjectHandler);
                }
                catch (Exception ex)
                {
                    _orchestrator.ReleaseDownloadSlot();
                    File.Delete(blockFile);
                    Logger.LogError(ex, "{DlName}: Error during download of {Id}", fi.Name, requestId);
                    ClearDownload();
                    return;
                }

                FileStream? fileBlockStream = null;
                try
                {
                    if (_downloadStatus.TryGetValue(fileGroup.Key, out var status))
                    {
                        status.TransferredFiles = 1;
                        status.DownloadStatus = DownloadStatus.Decompressing;
                    }

                    fileBlockStream = File.OpenRead(blockFile);
                    while (fileBlockStream.Position < fileBlockStream.Length)
                    {
                        (string fileHash, long fileLengthBytes) = ReadBlockFileHeader(fileBlockStream, enableFileObfuscation);

                        try
                        {
                            var fileExtension = fileReplacement.First(f =>
                                string.Equals(f.Hash, fileHash, StringComparison.OrdinalIgnoreCase)).GamePaths[0].Split(".")[^1];
                            var filePath = _fileDbManager.GetCacheFilePath(fileHash, fileExtension);

                            Logger.LogDebug("{DlName}: Decompressing {File}:{Le} => {Dest}", fi.Name, fileHash, fileLengthBytes, filePath);

                            byte[] compressedFileContent = new byte[fileLengthBytes];
                            var readBytes = await fileBlockStream.ReadAsync(compressedFileContent, CancellationToken.None).ConfigureAwait(false);
                            if (readBytes != fileLengthBytes)
                            {
                                throw new EndOfStreamException();
                            }

                            if(enableFileObfuscation)
                                MungeBuffer(compressedFileContent);

                            var decompressedFile = LZ4Wrapper.Unwrap(compressedFileContent);
                            await _fileCompactor.WriteAllBytesAsync(filePath, decompressedFile, CancellationToken.None).ConfigureAwait(false);

                            PersistFileToStorage(fileHash, filePath);
                        }
                        catch (EndOfStreamException)
                        {
                            Logger.LogWarning("{DlName}: Failure to extract file {FileHash}, stream ended prematurely", fi.Name, fileHash);
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning(e, "{DlName}: Error during decompression", fi.Name);
                        }
                    }
                }
                catch (EndOfStreamException)
                {
                    Logger.LogDebug("{DlName}: Failure to extract file header data, stream ended", fi.Name);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "{DlName}: Error during block file read", fi.Name);
                }
                finally
                {
                    _orchestrator.ReleaseDownloadSlot();
                    if (fileBlockStream != null)
                        await fileBlockStream.DisposeAsync().ConfigureAwait(false);
                    File.Delete(blockFile);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        Logger.LogDebug("Download end: {Id}", gameObjectHandler);

        ClearDownload();
    }

    private async Task DirectDownloadFilesInternal(int serverIndex, GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        // Separate out the files with direct download URLs
        var directDownloads = CurrentDownloads.Where(download => download.IsDirectDownload && !string.IsNullOrEmpty(download.DownloadUri.AbsoluteUri)).ToList();
        if(!directDownloads.Any())
            return;

        // Create download status trackers for the direct downloads
        foreach (var directDownload in directDownloads)
        {
            _downloadStatus[directDownload.DownloadUri.AbsoluteUri!] = new FileDownloadStatus()
            {
                DownloadStatus = DownloadStatus.Initializing,
                TotalBytes = directDownload.Total,
                TotalFiles = 1,
                TransferredBytes = 0,
                TransferredFiles = 0
            };
        }

        Logger.LogInformation("Downloading {Direct} files directly.", directDownloads.Count);
        Mediator.Publish(new DownloadStartedMessage(gameObjectHandler, _downloadStatus));

        // Start downloading each of the direct downloads
        var directDownloadsTask = directDownloads.Count == 0 ? Task.CompletedTask : Parallel.ForEachAsync(directDownloads, new ParallelOptions()
        {
            MaxDegreeOfParallelism = directDownloads.Count,
            CancellationToken = ct,
        },
        async (directDownload, token) =>
        {
            var directDownloadAbsoluteUri = directDownload.DownloadUri.AbsoluteUri;
            if (!_downloadStatus.TryGetValue(directDownloadAbsoluteUri, out var downloadTracker))
                return;

            Progress<long> progress = new((bytesDownloaded) =>
            {
                try
                {
                    if (!_downloadStatus.TryGetValue(directDownloadAbsoluteUri, out FileDownloadStatus? value)) return;
                    value.TransferredBytes += bytesDownloaded;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not set download progress");
                }
            });

            var tempFilename = _fileDbManager.GetCacheFilePath(directDownload.Hash, "bin");

            try
            {
                downloadTracker.DownloadStatus = DownloadStatus.WaitingForSlot;
                await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);

                // Download the compressed file directly
                downloadTracker.DownloadStatus = DownloadStatus.Downloading;
                Logger.LogDebug("{Hash} Beginning direct download of file from {Url}", directDownload.Hash, directDownloadAbsoluteUri);
                await DownloadFileThrottled(serverIndex,directDownload.DownloadUri, tempFilename, progress, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                Logger.LogDebug("{Hash}: Detected cancellation of direct download, discarding file.", directDownload.Hash);
                _orchestrator.ReleaseDownloadSlot();
                File.Delete(tempFilename);
                Logger.LogError(ex, "{Hash}: Error during direct download.", directDownload.Hash);
                ClearDownload();
                return;
            }
            catch (Exception ex)
            {
                _orchestrator.ReleaseDownloadSlot();
                File.Delete(tempFilename);
                Logger.LogError(ex, "{Hash}: Error during direct download.", directDownload.Hash);
                ClearDownload();
                return;
            }

            downloadTracker.TransferredFiles = 1;
            downloadTracker.DownloadStatus = DownloadStatus.Decompressing;

            try
            {
                var fileExtension = fileReplacement.First(f => string.Equals(f.Hash, directDownload.Hash, StringComparison.OrdinalIgnoreCase)).GamePaths[0].Split(".")[^1];
                var finalFilename = _fileDbManager.GetCacheFilePath(directDownload.Hash, fileExtension);
                Logger.LogDebug("Decompressing direct download {Hash} from {CompressedFile} to {FinalFile}", directDownload.Hash, tempFilename, finalFilename);
                byte[] compressedBytes = await File.ReadAllBytesAsync(tempFilename).ConfigureAwait(false);
                var decompressedBytes = LZ4Wrapper.Unwrap(compressedBytes);
                await _fileCompactor.WriteAllBytesAsync(finalFilename, decompressedBytes, CancellationToken.None).ConfigureAwait(false);
                PersistFileToStorage(directDownload.Hash, finalFilename);
                Logger.LogDebug("Finished direct download of {Hash}.", directDownload.Hash);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{Hash} Exception downloading from {Url}", directDownload.Hash, directDownloadAbsoluteUri);
            }
            finally
            {
                _orchestrator.ReleaseDownloadSlot();
                File.Delete(tempFilename);
            }
        });

        // Wait for all the batches and direct downloads to complete
        await directDownloadsTask.ConfigureAwait(false);

        Logger.LogDebug("Download end: {Id}", gameObjectHandler);

        ClearDownload();
    }

    private async Task DownloadFileThrottled(int serverIndex, Uri requestUrl, string destinationFilename, IProgress<long> progress, CancellationToken ct)
    {
        HttpResponseMessage response = null!;
        try
        {
            response = await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, requestUrl, ct, HttpCompletionOption.ResponseHeadersRead, withAuthToken: false).ConfigureAwait(false);

            var headersBuilder = new StringBuilder();
            if (response.RequestMessage != null)
            {
                headersBuilder.AppendLine("DefaultRequestHeaders:");
                foreach (var header in _orchestrator.DefaultRequestHeaders)
                {
                    foreach (var value in header.Value)
                    {
                        headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
                    }
                }
                headersBuilder.AppendLine("RequestMessage.Headers:");
                foreach (var header in response.RequestMessage.Headers)
                {
                    foreach (var value in header.Value)
                    {
                        headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
                    }
                }
                if (response.RequestMessage.Content != null)
                {
                    headersBuilder.AppendLine("RequestMessage.Content.Headers:");
                    foreach (var header in response.RequestMessage.Content.Headers)
                    {
                        foreach (var value in header.Value)
                        {
                            headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
                        }
                    }
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                // Dump some helpful debugging info
                string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Logger.LogWarning("Unsuccessful status code for {RequestUrl} is {StatusCode}, request headers: \n{Headers}\n, response text: \n\"{ResponseText}\"", requestUrl, response.StatusCode, headersBuilder.ToString(), responseText);

                // Raise an exception etc
                response.EnsureSuccessStatusCode();
            }
            else
            {
                Logger.LogDebug("Successful response for {RequestUrl} is {StatusCode}, request headers: \n{Headers}", requestUrl, response.StatusCode, headersBuilder.ToString());
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Error during download of {RequestUrl}, HttpStatusCode: {Code}", requestUrl, ex.StatusCode);
            if (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                throw new InvalidDataException($"Http error {ex.StatusCode} (cancelled: {ct.IsCancellationRequested}): {requestUrl}", ex);
            }

            return;
        }

        ThrottledStream? stream = null;
        try
        {
            var fileStream = File.Create(destinationFilename);
            await using (fileStream.ConfigureAwait(false))
            {
                var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 65536 : 8196;
                var buffer = new byte[bufferSize];

                var bytesRead = 0;
                var limit = _orchestrator.DownloadLimitPerSlot();
                Logger.LogTrace("Starting Download with a speed limit of {Limit} to {TempPath}", limit, destinationFilename);
                stream = new ThrottledStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit);
                _activeDownloadStreams.Add(stream);

                while ((bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);

                    progress.Report(bytesRead);
                }

                Logger.LogDebug("{RequestUrl} downloaded to {TempPath}", requestUrl, destinationFilename);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                if (!destinationFilename.IsNullOrEmpty())
                    File.Delete(destinationFilename);
            }
            catch
            {
                // ignore if file deletion fails
            }
            throw;
        }
        finally
        {
            if (stream != null)
            {
                _activeDownloadStreams.Remove(stream);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<List<DownloadFileDto>> FilesGetSizes(int serverIndex, List<string> hashes, CancellationToken ct)
    {
        var fileCdnUri = _orchestrator.GetFileCdnUri(serverIndex);
        if (fileCdnUri == null)
        {
            throw new InvalidOperationException("FileTransferManager is not initialized");
        }

        var enableFileObfuscation = _serverManager.GetServerByIndex(serverIndex)?.EnableObfuscationDownloadedFiles ?? false;

        Uri fileUri = enableFileObfuscation ? FilesRoutes.ServerFilesGetSizesFullPath(fileCdnUri) :
                                              FilesRoutes.ServerFilesGetSizesFullPath(fileCdnUri, GetTimeZoneUtcOffsetMinutes());

        var response = await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, fileUri, hashes, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<DownloadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private void PersistFileToStorage(string fileHash, string filePath)
    {
        var fi = new FileInfo(filePath);
        Func<DateTime> RandomDayInThePast()
        {
            DateTime start = new(1995, 1, 1, 1, 1, 1, DateTimeKind.Local);
            Random gen = new();
            int range = (DateTime.Today - start).Days;
            return () => start.AddDays(gen.Next(range));
        }

        fi.CreationTime = RandomDayInThePast().Invoke();
        fi.LastAccessTime = DateTime.Today;
        fi.LastWriteTime = RandomDayInThePast().Invoke();
        try
        {
            var entry = _fileDbManager.CreateCacheEntry(filePath);
            if (entry != null && !string.Equals(entry.Hash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("Hash mismatch after extracting, got {Hash}, expected {ExpectedHash}, deleting file", entry.Hash, fileHash);
                File.Delete(filePath);
                _fileDbManager.RemoveHashedFile(entry.Hash, entry.PrefixedFilePath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error creating cache entry");
        }
    }

    private async Task WaitForDownloadReady(int serverIndex, List<DownloadFileTransfer> downloadFileTransfer, Guid requestId, CancellationToken downloadCt)
    {
        bool alreadyCancelled = false;
        try
        {
            CancellationTokenSource localTimeoutCts = new();
            localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            CancellationTokenSource composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);

            while (!_orchestrator.IsDownloadReady(requestId))
            {
                try
                {
                    await Task.Delay(250, composite.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    if (downloadCt.IsCancellationRequested) throw;

                    var req = await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, FilesRoutes.RequestCheckQueueFullPath(downloadFileTransfer[0].DownloadUri, requestId),
                        downloadFileTransfer.Select(c => c.Hash).ToList(), downloadCt).ConfigureAwait(false);
                    req.EnsureSuccessStatusCode();
                    localTimeoutCts.Dispose();
                    composite.Dispose();
                    localTimeoutCts = new();
                    localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                    composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);
                }
            }

            localTimeoutCts.Dispose();
            composite.Dispose();

            Logger.LogDebug("Download {RequestId} ready", requestId);
        }
        catch (TaskCanceledException)
        {
            try
            {
                await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, FilesRoutes.RequestCancelFullPath(downloadFileTransfer[0].DownloadUri, requestId)).ConfigureAwait(false);
                alreadyCancelled = true;
            }
            catch
            {
                // ignore whatever happens here
            }

            throw;
        }
        finally
        {
            if (downloadCt.IsCancellationRequested && !alreadyCancelled)
            {
                try
                {
                    await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, FilesRoutes.RequestCancelFullPath(downloadFileTransfer[0].DownloadUri, requestId)).ConfigureAwait(false);
                }
                catch
                {
                    // ignore whatever happens here
                }
            }
            _orchestrator.ClearDownloadRequest(requestId);
        }
    }
}