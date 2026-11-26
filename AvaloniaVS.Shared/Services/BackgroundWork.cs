using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace AvaloniaVS.Services
{
    /// <summary>
    /// Starts fire-and-forget work (navigation, handler generation) on the package's
    /// <see cref="JoinableTaskFactory"/>, so VS knows about it and waits for it when the package
    /// shuts down, instead of on the global factory where it would be untracked (VSSDK007).
    /// Failures are logged and reported through VS's fault telemetry by name.
    /// </summary>
    internal static class BackgroundWork
    {
        public static void Start(Func<Task> work, string faultEventName)
        {
            var factory = AvaloniaPackage.Instance?.JoinableTaskFactory;

            if (factory is null)
            {
                // Only before the package has loaded (e.g. an editor feature used very early).
#pragma warning disable VSSDK007 // No package JoinableTaskCollection exists yet to track this.
                ThreadHelper.JoinableTaskFactory.RunAsync(work).FileAndForget(faultEventName);
#pragma warning restore VSSDK007
                return;
            }

            factory.RunAsync(work).FileAndForget(faultEventName);
        }
    }
}
