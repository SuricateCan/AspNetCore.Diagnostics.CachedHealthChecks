using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AspNetCore.Diagnostics.CachedHealthChecks
{
    internal class CacheableHealthChecksBuilder : IHealthChecksBuilder
    {
        private readonly IHealthChecksBuilder builder;
        private readonly List<HealthCheckRegistration> registrations = new List<HealthCheckRegistration>();
        private readonly CacheableHealthChecksServiceOptions options;

        public CacheableHealthChecksBuilder(IHealthChecksBuilder builder, CacheableHealthChecksServiceOptions options)
        {
            this.builder = builder;
            this.options = options;

            Services = builder.Services;
        }

        public IServiceCollection Services { get; }

        public IHealthChecksBuilder Add(HealthCheckRegistration registration)
        {
            builder.Add(registration);
            registrations.Add(registration);
            return this;
        }

        public void EnableCacheOnLastAdded(Action<RegistrationOptions> setup)
        {
            var registration = registrations.LastOrDefault();
            if (registration == null)
            {
                throw new InvalidOperationException("No registration was made. Perform a registration before enabling it's cache");
            }

            if (!registration.Tags.Add(options.Tag))
            {
                throw new InvalidOperationException($"There is already a tag {options.Tag} for registration {registration.Name}");
            }

            Services.Configure($"{registration.Name}", setup);
        }
    }
}