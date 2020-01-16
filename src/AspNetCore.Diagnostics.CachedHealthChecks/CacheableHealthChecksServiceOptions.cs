using System;

namespace AspNetCore.Diagnostics.CachedHealthChecks
{
    /// <summary>
    /// The options used by the <see cref="CacheableHealthCheckService"/>
    /// </summary>
    public sealed class CacheableHealthChecksServiceOptions
    {
        /// <summary>
        /// The tag used to identify cacheable registrations. Defaults to "cacheable".
        /// </summary>
        public string Tag { get; set; } = "cacheable";

        /// <summary>
        /// The prefix to use when caching a registration report
        /// </summary>
        public string CachePrefix { get; set; } = "cacheable-hc-";

        /// <summary>
        /// The default cache duration. Defaults to 10 minutes.
        /// </summary>
        public TimeSpan DefaultCacheDuration { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Indicate if checks that throw exceptions should be cached. Defaults to false.
        /// </summary>
        public bool CacheExceptions { get; set; } = false;

        /// <summary>
        /// When <see cref="CacheExceptions"/> is true, the checks that throw exception will be cached using this duration.
        /// If none is provided, <see cref="DefaultCacheDuration"/> will be used.
        /// </summary>
        public TimeSpan? CachedExceptionsDuration { get; set; }
    }
}