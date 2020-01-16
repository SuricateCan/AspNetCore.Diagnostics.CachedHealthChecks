using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AspNetCore.Diagnostics.CachedHealthChecks
{
    public static class Extensions
    {
        /// <summary>
        /// Add the <see cref="CacheableHealthCheckService"/>
        /// </summary>
        /// <param name="services"></param>
        /// <param name="setup"></param>
        /// <returns></returns>
        public static IHealthChecksBuilder AddHealthChecks(this IServiceCollection services, Action<CacheableHealthChecksServiceOptions> setup)
        {
            var builder = services.AddHealthChecks();
            services.RemoveAll<HealthCheckService>();
            services.AddSingleton<HealthCheckService, CacheableHealthCheckService>();
            services.Configure(setup);

            var options = new CacheableHealthChecksServiceOptions();
            setup(options);

            return new CacheableHealthChecksBuilder(builder, options);
        }

        /// <summary>
        /// Enable the cache of the last added health check
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static IHealthChecksBuilder EnableCache(this IHealthChecksBuilder builder) =>
            builder.EnableCache(opts => { });

        /// <summary>
        /// Enable the cache of the last added health check
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="setup"></param>
        /// <returns></returns>
        public static IHealthChecksBuilder EnableCache(this IHealthChecksBuilder builder, Action<RegistrationOptions> setup)
        {
            if (!(builder is CacheableHealthChecksBuilder cacheableBuilder))
                throw new InvalidOperationException("Call services.AddHealthChecks() overload with cache options setup");

            cacheableBuilder.EnableCacheOnLastAdded(setup);

            return builder;
        }
    }
}