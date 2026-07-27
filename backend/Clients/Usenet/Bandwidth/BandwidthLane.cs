namespace NzbWebDAV.Clients.Usenet.Bandwidth;

/// <summary>
/// Streaming = an interactive WebDAV/playback read (<see cref="Contexts.DownloadPriorityContext"/>
/// with <see cref="Concurrency.SemaphorePriority.High"/>). Queue = everything else, including
/// queue-import downloads (<see cref="Contexts.QueueDownloadContext"/>) and background prefetch
/// (which carries neither context and so resolves to Queue by default).
/// </summary>
public enum BandwidthLane
{
    Streaming,
    Queue,
}
