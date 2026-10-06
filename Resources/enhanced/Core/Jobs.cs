using System.Collections;

namespace HSAEnhanced.Core
{
    // coroutines of ours, on our own object
    static class Jobs
    {
        internal static void Run(IEnumerator routine)
        {
            Host.Run(routine);
        }
    }
}
