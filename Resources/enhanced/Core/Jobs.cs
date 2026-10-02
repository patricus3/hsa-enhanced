using System.Collections;

namespace HSAEnhanced.Core
{
    // coroutines of ours, on our own object
    static class Jobs
    {
        internal static void Run(IEnumerator routine)
        {
#if WITHOUT_HSA
            Host.Run(routine);
#else
            FallbackWatcher.Run(routine);
#endif
        }
    }
}
