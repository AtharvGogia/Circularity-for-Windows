using System;
using System.Threading;
using System.Threading.Tasks;

namespace Circularity.Engine;

/// <summary>
/// Runs work on a fresh single-threaded-apartment thread. WPF imaging (RenderTargetBitmap in
/// particular) and Office automation both require one.
/// </summary>
internal static class Sta
{
    public static Task<T> Run<T>(Func<T> work, string name)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
