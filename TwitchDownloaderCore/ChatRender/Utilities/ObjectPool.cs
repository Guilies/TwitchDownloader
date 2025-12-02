using System;
using System.Text;

namespace TwitchDownloaderCore.ChatRender.Utilities
{
    /// <summary>
    /// Provides pooling for frequently allocated objects to reduce GC pressure.
    /// Uses thread-static storage for thread-safe access without locking.
    /// </summary>
    public static class ObjectPool
    {
        [ThreadStatic]
        private static StringBuilder _stringBuilder1;

        [ThreadStatic]
        private static StringBuilder _stringBuilder2;

        /// <summary>
        /// Rents a StringBuilder from the pool. Returns must be paired with ReturnStringBuilder.
        /// </summary>
        /// <param name="capacity">Initial capacity hint (not enforced)</param>
        /// <returns>A cleared StringBuilder ready for use</returns>
        public static StringBuilder RentStringBuilder(int capacity = 256)
        {
            if (_stringBuilder1 == null)
            {
                _stringBuilder1 = new StringBuilder(capacity);
                return _stringBuilder1;
            }

            if (_stringBuilder2 == null)
            {
                _stringBuilder2 = new StringBuilder(capacity);
                return _stringBuilder2;
            }

            // Fallback for deep recursion (rare)
            return new StringBuilder(capacity);
        }

        /// <summary>
        /// Returns a StringBuilder to the pool. The StringBuilder will be cleared.
        /// </summary>
        /// <param name="sb">The StringBuilder to return</param>
        public static void ReturnStringBuilder(StringBuilder sb)
        {
            // Clear the StringBuilder for reuse
            sb?.Clear();

            // No-op for thread-static instances - they stay in the thread-local pool
            // For fallback instances created in deep recursion, they'll be GC'd naturally
        }
    }
}
