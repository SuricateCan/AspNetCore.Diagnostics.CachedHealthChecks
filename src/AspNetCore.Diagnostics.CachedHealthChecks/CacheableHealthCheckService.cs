using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AspNetCore.Diagnostics.CachedHealthChecks
{
    internal class CacheableHealthCheckService : HealthCheckService
    {
        private readonly IServiceScopeFactory scopeFactory;
        private readonly IOptions<HealthCheckServiceOptions> baseOptions;
        private readonly IOptions<CacheableHealthChecksServiceOptions> cacheOptions;
        private readonly ILogger<CacheableHealthCheckService> logger;
        private readonly IMemoryCache memoryCache;

        public CacheableHealthCheckService(
            IServiceScopeFactory scopeFactory,
            IOptions<HealthCheckServiceOptions> baseOptions,
            IOptions<CacheableHealthChecksServiceOptions> cacheOptions,
            ILogger<CacheableHealthCheckService> logger,
            IMemoryCache memoryCache)
        {
            this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            this.baseOptions = baseOptions ?? throw new ArgumentNullException(nameof(baseOptions));
            this.cacheOptions = cacheOptions ?? throw new ArgumentNullException(nameof(cacheOptions));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));

            ValidateRegistrations(baseOptions.Value.Registrations.ToArray());
        }

#if NETCOREAPP2_2

        public override async Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool> predicate, CancellationToken cancellationToken = default)
        {
            var registrations = baseOptions.Value.Registrations;
            var cacheOptionsValue = cacheOptions.Value;

            using (var scope = scopeFactory.CreateScope())
            {
                var context = new HealthCheckContext();
                var entries = new Dictionary<string, HealthReportEntry>(StringComparer.OrdinalIgnoreCase);
                var optionsSnapshot = scope.ServiceProvider.GetService<IOptionsSnapshot<RegistrationOptions>>();

                var totalTime = ValueStopwatch.StartNew();
                Log.HealthCheckProcessingBegin(logger);

                foreach (var registration in registrations)
                {
                    if (predicate != null && !predicate(registration))
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // If the health check does things like make Database queries using EF or backend HTTP calls,
                    // it may be valuable to know that logs it generates are part of a health check. So we start a scope.
                    using (logger.BeginScope(new HealthCheckLogScope(registration.Name)))
                    {
                        var cacheable = registration.Tags.Any(a => a == cacheOptionsValue.Tag);
                        var cacheKey = $"{cacheOptionsValue.CachePrefix}{registration.Name}";

                        if (memoryCache.TryGetValue<HealthReportEntry>(cacheKey, out var cachedEntry))
                        {
                            Log.HealthCheckFromCache(logger, registration, cachedEntry);
                            Log.HealthCheckData(logger, registration, cachedEntry);
                            entries[registration.Name] = cachedEntry;
                            continue;
                        }

                        var healthCheck = registration.Factory(scope.ServiceProvider);

                        var stopwatch = ValueStopwatch.StartNew();
                        context.Registration = registration;

                        Log.HealthCheckBegin(logger, registration);

                        HealthReportEntry entry;
                        try
                        {
                            var result = await healthCheck.CheckHealthAsync(context, cancellationToken);
                            var duration = stopwatch.GetElapsedTime();

                            if (cacheable)
                            {
                                entry = BuildAndCacheReportEntry(cacheKey, registration, cacheOptionsValue, optionsSnapshot, result, duration);
                            }
                            else
                            {
                                entry = new HealthReportEntry(
                                    status: result.Status,
                                    description: result.Description,
                                    duration: duration,
                                    exception: result.Exception,
                                    data: result.Data);
                            }

                            Log.HealthCheckEnd(logger, registration, entry, duration);
                            Log.HealthCheckData(logger, registration, entry);
                        }
                        // Allow cancellation to propagate.
                        catch (Exception ex) when (ex as OperationCanceledException == null)
                        {
                            var duration = stopwatch.GetElapsedTime();

                            if (cacheable && cacheOptionsValue.CacheExceptions)
                            {
                                entry = BuildAndCacheExceptionReport(cacheKey, registration, cacheOptionsValue, optionsSnapshot, ex, duration);
                            }
                            else
                            {
                                entry = new HealthReportEntry(
                                    status: HealthStatus.Unhealthy,
                                    description: ex.Message,
                                    duration: duration,
                                    exception: ex,
                                    data: null);
                            }

                            Log.HealthCheckError(logger, registration, ex, duration);
                        }

                        entries[registration.Name] = entry;
                    }
                }

                var totalElapsedTime = totalTime.GetElapsedTime();
                var healthReport = new HealthReport(entries, totalElapsedTime);
                Log.HealthCheckProcessingEnd(logger, healthReport.Status, totalElapsedTime);
                return healthReport;
            }
        }

#elif NETCOREAPP3_1

        public override async Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool> predicate, CancellationToken cancellationToken = default)
        {
            var registrations = baseOptions.Value.Registrations;
            var cacheOptionsValue = cacheOptions.Value;

            if (predicate != null)
            {
                registrations = registrations.Where(predicate).ToArray();
            }

            var totalTime = ValueStopwatch.StartNew();
            Log.HealthCheckProcessingBegin(logger);

            var tasks = new Task<HealthReportEntry>[registrations.Count];
            var index = 0;
            using (var scope = scopeFactory.CreateScope())
            {
                foreach (var registration in registrations)
                {
                    tasks[index++] = Task.Run(() => RunCheckAsync(scope, registration, cacheOptionsValue, cancellationToken), cancellationToken);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            index = 0;
            var entries = new Dictionary<string, HealthReportEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var registration in registrations)
            {
                entries[registration.Name] = tasks[index++].Result;
            }

            var totalElapsedTime = totalTime.GetElapsedTime();
            var report = new HealthReport(entries, totalElapsedTime);
            Log.HealthCheckProcessingEnd(logger, report.Status, totalElapsedTime);
            return report;
        }

        private async Task<HealthReportEntry> RunCheckAsync(IServiceScope scope, HealthCheckRegistration registration, CacheableHealthChecksServiceOptions cacheOptionsValue, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // If the health check does things like make Database queries using EF or backend HTTP calls,
            // it may be valuable to know that logs it generates are part of a health check. So we start a scope.
            using (logger.BeginScope(new HealthCheckLogScope(registration.Name)))
            {
                var cacheKey = $"{cacheOptionsValue.CachePrefix}{registration.Name}";

                if (memoryCache.TryGetValue(cacheKey, out HealthReportEntry entry))
                {
                    Log.HealthCheckFromCache(logger, registration, entry);
                    Log.HealthCheckData(logger, registration, entry);
                }
                else
                {
                    var cacheable = registration.Tags.Any(a => a == cacheOptionsValue.Tag);
                    var optionsSnapshot = scope.ServiceProvider.GetService<IOptionsSnapshot<RegistrationOptions>>();

                    var healthCheck = registration.Factory(scope.ServiceProvider);

                    var stopwatch = ValueStopwatch.StartNew();
                    var context = new HealthCheckContext { Registration = registration };

                    Log.HealthCheckBegin(logger, registration);

                    CancellationTokenSource timeoutCancellationTokenSource = null;
                    try
                    {
                        HealthCheckResult result;

                        var checkCancellationToken = cancellationToken;
                        if (registration.Timeout > TimeSpan.Zero)
                        {
                            timeoutCancellationTokenSource =
                                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            timeoutCancellationTokenSource.CancelAfter(registration.Timeout);
                            checkCancellationToken = timeoutCancellationTokenSource.Token;
                        }

                        result = await healthCheck.CheckHealthAsync(context, checkCancellationToken)
                            .ConfigureAwait(false);

                        var duration = stopwatch.GetElapsedTime();

                        if (cacheable)
                        {
                            entry = BuildAndCacheReportEntry(cacheKey, registration, cacheOptionsValue, optionsSnapshot, result, duration);
                        }
                        else
                        {
                            entry = new HealthReportEntry(
                                status: result.Status,
                                description: result.Description,
                                duration: duration,
                                exception: result.Exception,
                                data: result.Data,
                                tags: registration.Tags);
                        }

                        Log.HealthCheckEnd(logger, registration, entry, duration);
                        Log.HealthCheckData(logger, registration, entry);
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        var duration = stopwatch.GetElapsedTime();
                        if (cacheable && cacheOptionsValue.CacheExceptions)
                        {
                            entry = BuildAndCacheExceptionReport(cacheKey, registration, cacheOptionsValue, optionsSnapshot, ex, duration);
                        }
                        else
                        {
                            entry = new HealthReportEntry(
                                status: HealthStatus.Unhealthy,
                                description: "A timeout occured while running check.",
                                duration: duration,
                                exception: ex,
                                data: null);
                        }

                        Log.HealthCheckError(logger, registration, ex, duration);
                    }

                    // Allow cancellation to propagate if it's not a timeout.
                    catch (Exception ex) when (ex as OperationCanceledException == null)
                    {
                        var duration = stopwatch.GetElapsedTime();
                        if (cacheable && cacheOptionsValue.CacheExceptions)
                        {
                            entry = BuildAndCacheExceptionReport(cacheKey, registration, cacheOptionsValue, optionsSnapshot, ex, duration);
                        }
                        else
                        {
                            entry = new HealthReportEntry(
                                status: HealthStatus.Unhealthy,
                                description: ex.Message,
                                duration: duration,
                                exception: ex,
                                data: null);
                        }

                        Log.HealthCheckError(logger, registration, ex, duration);
                    }
                    finally
                    {
                        timeoutCancellationTokenSource?.Dispose();
                    }
                }

                return entry;
            }
        }

#endif

        private HealthReportEntry BuildAndCacheExceptionReport(string cacheKey, HealthCheckRegistration registration,
            CacheableHealthChecksServiceOptions cacheOptionsValue,
            IOptionsSnapshot<RegistrationOptions> optionsSnapshot, Exception exception, TimeSpan duration)
        {
            var opts = optionsSnapshot.Get(registration.Name);
            var expiresIn = opts.ExceptionCacheDuration ??
                            cacheOptionsValue.CachedExceptionsDuration ?? cacheOptionsValue.DefaultCacheDuration;

            return BuildAndCacheReportEntry(cacheKey, expiresIn, HealthStatus.Unhealthy, exception.Message, duration,
                exception, new Dictionary<string, object>());
        }

        private HealthReportEntry BuildAndCacheReportEntry(string cacheKey, HealthCheckRegistration registration,
            CacheableHealthChecksServiceOptions cacheOptionsValue,
            IOptionsSnapshot<RegistrationOptions> optionsSnapshot, HealthCheckResult result, TimeSpan duration)
        {
            var opts = optionsSnapshot.Get(registration.Name);
            var expiresIn = opts.CacheDuration ?? cacheOptionsValue.DefaultCacheDuration;

            return BuildAndCacheReportEntry(cacheKey, expiresIn, result.Status, result.Description, duration,
                result.Exception, result.Data);
        }

        private HealthReportEntry BuildAndCacheReportEntry(string cacheKey, TimeSpan expiresIn, HealthStatus healthStatus,
            string description, TimeSpan duration, Exception exception, IReadOnlyDictionary<string, object> healthData)
        {
            var expiresAt = DateTime.UtcNow.Add(expiresIn);

            var data = new Dictionary<string, object>(healthData)
            {
                {
                    "cached",
                    $"This response was resolved in {DateTime.UtcNow:R} and will remain in cache until {expiresAt:R}"
                }
            };

            var entry = new HealthReportEntry(
                status: healthStatus,
                description: description,
                duration: duration,
                exception: exception,
                data: data);

            memoryCache.Set(cacheKey, entry, expiresAt);

            return entry;
        }

        private static void ValidateRegistrations(HealthCheckRegistration[] registrations)
        {
            // Scan the list for duplicate names to provide a better error if there are duplicates.
            var duplicateNames = registrations
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();

            if (duplicateNames.Length > 0)
            {
                throw new ArgumentException($"Duplicate health checks were registered with the name(s): {string.Join(", ", duplicateNames)}", nameof(registrations));
            }
        }

        internal static class EventIds
        {
            public static readonly EventId HealthCheckProcessingBegin = new EventId(100, "HealthCheckProcessingBegin");
            public static readonly EventId HealthCheckProcessingEnd = new EventId(101, "HealthCheckProcessingEnd");

            public static readonly EventId HealthCheckBegin = new EventId(102, "HealthCheckBegin");
            public static readonly EventId HealthCheckEnd = new EventId(103, "HealthCheckEnd");
            public static readonly EventId HealthCheckError = new EventId(104, "HealthCheckError");
            public static readonly EventId HealthCheckData = new EventId(105, "HealthCheckData");
            public static readonly EventId HealthCheckFromCache = new EventId(106, "HealthCheckFromCache");
        }

        private static class Log
        {
            private static readonly Action<ILogger, Exception> healthCheckProcessingBegin = LoggerMessage.Define(
                LogLevel.Debug,
                EventIds.HealthCheckProcessingBegin,
                "Running health checks");

            private static readonly Action<ILogger, double, HealthStatus, Exception> healthCheckProcessingEnd = LoggerMessage.Define<double, HealthStatus>(
                LogLevel.Debug,
                EventIds.HealthCheckProcessingEnd,
                "Health check processing completed after {ElapsedMilliseconds}ms with combined status {HealthStatus}");

            private static readonly Action<ILogger, string, Exception> healthCheckBegin = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.HealthCheckBegin,
                "Running health check {HealthCheckName}");

            // These are separate so they can have different log levels
            private static readonly string HealthCheckEndText = "Health check {HealthCheckName} completed after {ElapsedMilliseconds}ms with status {HealthStatus} and '{HealthCheckDescription}'";

            private static readonly Action<ILogger, string, double, HealthStatus, string, Exception> healthCheckEndHealthy = LoggerMessage.Define<string, double, HealthStatus, string>(
                LogLevel.Debug,
                EventIds.HealthCheckEnd,
                HealthCheckEndText);

            private static readonly Action<ILogger, string, double, HealthStatus, string, Exception> healthCheckEndDegraded = LoggerMessage.Define<string, double, HealthStatus, string>(
                LogLevel.Warning,
                EventIds.HealthCheckEnd,
                HealthCheckEndText);

            private static readonly Action<ILogger, string, double, HealthStatus, string, Exception> healthCheckEndUnhealthy = LoggerMessage.Define<string, double, HealthStatus, string>(
                LogLevel.Error,
                EventIds.HealthCheckEnd,
                HealthCheckEndText);

            private static readonly Action<ILogger, string, double, Exception> healthCheckError = LoggerMessage.Define<string, double>(
                LogLevel.Error,
                EventIds.HealthCheckError,
                "Health check {HealthCheckName} threw an unhandled exception after {ElapsedMilliseconds}ms");

            private static readonly Action<ILogger, string, Exception> healthCheckFromCache = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.HealthCheckFromCache,
                "Result for {HealthCheckName} was taken from cache");

            public static void HealthCheckProcessingBegin(ILogger logger)
            {
                healthCheckProcessingBegin(logger, null);
            }

            public static void HealthCheckProcessingEnd(ILogger logger, HealthStatus status, TimeSpan duration)
            {
                healthCheckProcessingEnd(logger, duration.TotalMilliseconds, status, null);
            }

            public static void HealthCheckBegin(ILogger logger, HealthCheckRegistration registration)
            {
                healthCheckBegin(logger, registration.Name, null);
            }

            public static void HealthCheckEnd(ILogger logger, HealthCheckRegistration registration, HealthReportEntry entry, TimeSpan duration)
            {
                switch (entry.Status)
                {
                    case HealthStatus.Healthy:
                        healthCheckEndHealthy(logger, registration.Name, duration.TotalMilliseconds, entry.Status, entry.Description, null);
                        break;

                    case HealthStatus.Degraded:
                        healthCheckEndDegraded(logger, registration.Name, duration.TotalMilliseconds, entry.Status, entry.Description, null);
                        break;

                    case HealthStatus.Unhealthy:
                        healthCheckEndUnhealthy(logger, registration.Name, duration.TotalMilliseconds, entry.Status, entry.Description, null);
                        break;
                }
            }

            public static void HealthCheckError(ILogger logger, HealthCheckRegistration registration, Exception exception, TimeSpan duration)
            {
                healthCheckError(logger, registration.Name, duration.TotalMilliseconds, exception);
            }

            public static void HealthCheckData(ILogger logger, HealthCheckRegistration registration, HealthReportEntry entry)
            {
                if (entry.Data.Count > 0 && logger.IsEnabled(LogLevel.Debug))
                {
                    logger.Log(
                        LogLevel.Debug,
                        EventIds.HealthCheckData,
                        new HealthCheckDataLogValue(registration.Name, entry.Data),
                        null,
                        (state, ex) => state.ToString());
                }
            }

            public static void HealthCheckFromCache(ILogger logger, HealthCheckRegistration registration, HealthReportEntry entry)
            {
                healthCheckFromCache(logger, registration.Name, null);
            }
        }

        internal class HealthCheckDataLogValue : IReadOnlyList<KeyValuePair<string, object>>
        {
            private readonly string name;
            private readonly List<KeyValuePair<string, object>> values;

            private string formatted;

            public HealthCheckDataLogValue(string name, IReadOnlyDictionary<string, object> values)
            {
                this.name = name;
                this.values = values.ToList();

                // We add the name as a kvp so that you can filter by health check name in the logs.
                // This is the same parameter name used in the other logs.
                this.values.Add(new KeyValuePair<string, object>("HealthCheckName", name));
            }

            public KeyValuePair<string, object> this[int index]
            {
                get
                {
                    if (index < 0 || index >= Count)
                    {
                        throw new IndexOutOfRangeException(nameof(index));
                    }

                    return values[index];
                }
            }

            public int Count => values.Count;

            public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
            {
                return values.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return values.GetEnumerator();
            }

            public override string ToString()
            {
                if (formatted != null)
                    return formatted;

                var builder = new StringBuilder();
                builder.AppendLine($"Health check data for {name}:");

                foreach (var kvp in values)
                {
                    builder.Append("    ");
                    builder.Append(kvp.Key);
                    builder.Append(": ");

                    builder.AppendLine(kvp.Value?.ToString());
                }

                formatted = builder.ToString();
                return formatted;
            }
        }

        internal class HealthCheckLogScope : IReadOnlyList<KeyValuePair<string, object>>
        {
            public string HealthCheckName { get; }

            int IReadOnlyCollection<KeyValuePair<string, object>>.Count { get; } = 1;

            KeyValuePair<string, object> IReadOnlyList<KeyValuePair<string, object>>.this[int index]
            {
                get
                {
                    if (index == 0)
                    {
                        return new KeyValuePair<string, object>(nameof(HealthCheckName), HealthCheckName);
                    }

                    throw new ArgumentOutOfRangeException(nameof(index));
                }
            }

            /// <summary>
            /// Creates a new instance of <see cref="HealthCheckLogScope"/> with the provided name.
            /// </summary>
            /// <param name="healthCheckName">The name of the health check being executed.</param>
            public HealthCheckLogScope(string healthCheckName)
            {
                HealthCheckName = healthCheckName;
            }

            IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator()
            {
                yield return new KeyValuePair<string, object>(nameof(HealthCheckName), HealthCheckName);
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return ((IEnumerable<KeyValuePair<string, object>>)this).GetEnumerator();
            }
        }

        internal struct ValueStopwatch
        {
            private static readonly double TimestampToTicks = 10000000.0 / Stopwatch.Frequency;
            private readonly long startTimestamp;

            public bool IsActive => (ulong)startTimestamp > 0UL;

            private ValueStopwatch(long startTimestamp)
            {
                this.startTimestamp = startTimestamp;
            }

            public static ValueStopwatch StartNew()
            {
                return new ValueStopwatch(Stopwatch.GetTimestamp());
            }

            public TimeSpan GetElapsedTime()
            {
                if (!IsActive)
                    throw new InvalidOperationException("An uninitialized, or 'default', ValueStopwatch cannot be used to get elapsed time.");
                var num = Stopwatch.GetTimestamp() - startTimestamp;
                return new TimeSpan((long)(TimestampToTicks * num));
            }
        }
    }
}