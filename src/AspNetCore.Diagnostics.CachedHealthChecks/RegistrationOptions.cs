using System;

namespace AspNetCore.Diagnostics.CachedHealthChecks
{
    /// <summary>
    /// The options for each registration
    /// </summary>
    public sealed class RegistrationOptions
    {
        /// <summary>
        /// The cache duration for this registration
        /// </summary>
        public TimeSpan? CacheDuration { get; set; }

        /// <summary>
        /// The exception cache duration for this registration. Only used when <see cref="CacheableHealthChecksServiceOptions.CacheExceptions"/> is enabled.
        /// </summary>
        public TimeSpan? ExceptionCacheDuration { get; set; }
    }
}