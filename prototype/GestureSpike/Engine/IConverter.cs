using System;
using System.Threading;
using System.Threading.Tasks;

namespace GestureSpike.Engine;

/// <summary>One conversion backend (WIC for images, FFmpeg for video/audio, ...).</summary>
internal interface IConverter
{
    /// <summary>Whether this backend handles turning <paramref name="inputPath"/> into <paramref name="target"/>.</summary>
    bool CanConvert(string inputPath, string target);

    /// <summary>
    /// Writes the converted file to <paramref name="outputPath"/>, which already exists as an empty
    /// placeholder and may be overwritten. Reports progress from 0 to 1 and honours cancellation.
    /// The caller deletes the output if this throws.
    /// </summary>
    Task ConvertAsync(string inputPath, string target, string outputPath, IProgress<double> progress, CancellationToken cancellationToken);
}
