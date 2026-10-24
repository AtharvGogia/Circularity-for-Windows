using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace GestureSpike.Engine;

internal enum JobState { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>One operation (convert, resize, crop) applied to one file.</summary>
internal sealed class ConversionJob
{
    internal ConversionJob(ConversionBatch batch, string inputPath, Operation operation, IConverter? converter)
    {
        Batch = batch;
        InputPath = inputPath;
        Operation = operation;
        Converter = converter;
    }

    public ConversionBatch Batch { get; }
    public string InputPath { get; }
    public Operation Operation { get; }

    /// <summary>The file written, once the job has succeeded.</summary>
    public string? OutputPath { get; internal set; }

    public JobState State { get; internal set; } = JobState.Queued;

    /// <summary>0 to 1. Stays at 0 for converters that can't measure progress until they finish.</summary>
    public double Progress { get; internal set; }

    public string? Error { get; internal set; }

    public bool IsFinished => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled;

    internal IConverter? Converter { get; }
    internal CancellationTokenSource Cancellation { get; } = new();
}

/// <summary>All the jobs from one drop on the wheel, so they can be reported together.</summary>
internal sealed class ConversionBatch
{
    private int _remaining;

    public IReadOnlyList<ConversionJob> Jobs { get; private set; } = Array.Empty<ConversionJob>();

    internal void SetJobs(IReadOnlyList<ConversionJob> jobs)
    {
        Jobs = jobs;
        _remaining = jobs.Count;
    }

    /// <summary>Returns true for exactly one caller: whoever finishes the batch's last job.</summary>
    internal bool MarkOneFinished() => Interlocked.Decrement(ref _remaining) == 0;
}

/// <summary>
/// Runs conversions in the background, a few at a time, with progress and cancellation.
/// Events are raised on worker threads; UI code must marshal them to its own thread.
/// </summary>
internal sealed class ConversionQueue : IDisposable
{
    /// <summary>A job was queued, started, made progress or finished.</summary>
    public event Action<ConversionJob>? JobChanged;

    /// <summary>Every job in a batch has finished (in any state).</summary>
    public event Action<ConversionBatch>? BatchFinished;

    private readonly IReadOnlyList<IConverter> _converters;
    private readonly Channel<ConversionJob> _pending = Channel.CreateUnbounded<ConversionJob>();
    private readonly List<ConversionJob> _unfinished = new();
    private readonly Task[] _workers;

    /// <param name="maxParallel">
    /// How many jobs run at once. FFmpeg already uses every core for one video, so this is
    /// kept small; it mainly stops a long video from holding up quick image conversions.
    /// </param>
    public ConversionQueue(IEnumerable<IConverter> converters, int maxParallel = 2)
    {
        _converters = converters.ToList();
        _workers = Enumerable.Range(0, maxParallel).Select(_ => Task.Run(WorkerLoop)).ToArray();
    }

    /// <summary>Snapshot of the jobs that are queued or running.</summary>
    public IReadOnlyList<ConversionJob> Unfinished
    {
        get
        {
            lock (_unfinished)
                return _unfinished.ToList();
        }
    }

    /// <summary>Applies the same operation to every file.</summary>
    public ConversionBatch Enqueue(IReadOnlyList<string> files, Operation operation) =>
        Enqueue(files.Select(file => (file, operation)).ToList());

    /// <summary>Queues one batch where each file can have its own operation (e.g. its own crop).</summary>
    public ConversionBatch Enqueue(IReadOnlyList<(string File, Operation Operation)> items)
    {
        var batch = new ConversionBatch();
        var jobs = items
            .Select(item => new ConversionJob(batch, item.File, item.Operation,
                _converters.FirstOrDefault(c => c.CanHandle(item.File, item.Operation))))
            .ToList();
        batch.SetJobs(jobs);

        foreach (var job in jobs)
        {
            if (job.Converter == null)
            {
                Finish(job, JobState.Failed, UnsupportedMessage(job));
                continue;
            }

            lock (_unfinished)
                _unfinished.Add(job);
            JobChanged?.Invoke(job);
            _pending.Writer.TryWrite(job);
        }

        return batch;
    }

    public void CancelAll()
    {
        foreach (var job in Unfinished)
            job.Cancellation.Cancel();
    }

    private static string UnsupportedMessage(ConversionJob job)
    {
        string ext = Path.GetExtension(job.InputPath).TrimStart('.').ToUpperInvariant();
        return job.Operation is ConvertTo convert
            ? $"Converting {ext} to {convert.Target.ToUpperInvariant()} isn't supported yet."
            : $"{job.Operation.Describe()} isn't supported for {ext} files yet.";
    }

    private async Task WorkerLoop()
    {
        await foreach (var job in _pending.Reader.ReadAllAsync())
            await Run(job);
    }

    private async Task Run(ConversionJob job)
    {
        if (job.Cancellation.IsCancellationRequested)
        {
            Finish(job, JobState.Cancelled, null);
            return;
        }

        string? output = null;
        try
        {
            var converter = job.Converter!;
            output = OutputPaths.Reserve(job.InputPath, converter.OutputExtension(job.InputPath, job.Operation), job.Operation.NameSuffix);
            job.State = JobState.Running;
            JobChanged?.Invoke(job);

            var progress = new InlineProgress(value =>
            {
                job.Progress = value;
                JobChanged?.Invoke(job);
            });
            await converter.RunAsync(job.InputPath, job.Operation, output, progress, job.Cancellation.Token);

            job.OutputPath = output;
            job.Progress = 1;
            Finish(job, JobState.Succeeded, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            Finish(job, JobState.Cancelled, null);
        }
        catch (Exception ex)
        {
            TryDelete(output);
            Finish(job, JobState.Failed, ex.Message);
        }
    }

    private void Finish(ConversionJob job, JobState state, string? error)
    {
        job.State = state;
        job.Error = error;
        lock (_unfinished)
            _unfinished.Remove(job);
        JobChanged?.Invoke(job);

        if (job.Batch.MarkOneFinished())
            BatchFinished?.Invoke(job.Batch);
    }

    private static void TryDelete(string? path)
    {
        if (path == null)
            return;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover partial file is better than crashing the worker.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Stops accepting work, cancels everything and waits briefly for workers to clean up.</summary>
    public void Dispose()
    {
        _pending.Writer.TryComplete();
        CancelAll();
        Task.WaitAll(_workers, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Calls back on the reporting thread. <see cref="Progress{T}"/> would post to the thread pool
    /// instead, so updates could arrive out of order or after the job had finished.
    /// </summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
