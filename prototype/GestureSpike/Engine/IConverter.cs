using System;
using System.Threading;
using System.Threading.Tasks;

namespace GestureSpike.Engine;

/// <summary>One conversion backend (WIC for images, FFmpeg for video/audio, ...).</summary>
internal interface IConverter
{
    /// <summary>Whether this backend can apply <paramref name="operation"/> to <paramref name="inputPath"/>.</summary>
    bool CanHandle(string inputPath, Operation operation);

    /// <summary>Extension (no dot) of the file the operation produces, e.g. "png".</summary>
    string OutputExtension(string inputPath, Operation operation);

    /// <summary>
    /// Writes the result to <paramref name="outputPath"/>, which already exists as an empty
    /// placeholder and may be overwritten. Reports progress from 0 to 1 and honours cancellation.
    /// The caller deletes the output if this throws.
    /// </summary>
    Task RunAsync(string inputPath, Operation operation, string outputPath, IProgress<double> progress, CancellationToken cancellationToken);
}
