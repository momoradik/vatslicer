namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Estimates memory and disk usage for a slice job.
/// Helps prevent OOM errors by warning before slicing starts.
/// </summary>
public static class SliceMemoryEstimator
{
    public sealed record MemoryEstimate
    {
        public required long LayerImageSizeBytes { get; init; }
        public required long TotalDiskMb { get; init; }
        public required long PeakMemoryMb { get; init; }
        public required bool MayExceedMemory { get; init; }
        public required string Warning { get; init; }
    }

    public static MemoryEstimate Estimate(
        int resolutionX, int resolutionY,
        int totalLayers,
        bool antiAlias = false,
        long availableMemoryMb = 4096)
    {
        long bytesPerPixel = antiAlias ? 1 : 1; // Gray8 either way
        long rawLayerBytes = (long)resolutionX * resolutionY * bytesPerPixel;
        long pngLayerBytes = rawLayerBytes / 4; // PNG compression ~4:1 for binary images

        long totalDiskBytes = pngLayerBytes * totalLayers;
        long totalDiskMb = totalDiskBytes / (1024 * 1024);

        // Peak memory: ~3 layers in memory at once (current + prev + render buffer)
        long peakBytes = rawLayerBytes * 3 + pngLayerBytes * 2;
        long peakMb = peakBytes / (1024 * 1024);

        bool mayExceed = peakMb > availableMemoryMb * 0.8;
        string warning = mayExceed
            ? $"Slice job may use {peakMb}MB peak memory (available: {availableMemoryMb}MB). Consider reducing resolution."
            : $"Memory OK: ~{peakMb}MB peak, {totalDiskMb}MB disk for {totalLayers} layers";

        return new MemoryEstimate
        {
            LayerImageSizeBytes = rawLayerBytes,
            TotalDiskMb = totalDiskMb,
            PeakMemoryMb = peakMb,
            MayExceedMemory = mayExceed,
            Warning = warning,
        };
    }
}
