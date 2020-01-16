# AspNetCore.Diagnostics.CachedHealthChecks
This lib is an extension built on top of the _Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService_ that allows the developer to choose to cache any checks they want.

## Installation
To install this lib, simply install the package from **NuGet** using the command below.
```shell
Install-Package AspNetCore.Diagnostics.CachedHealthChecks
```

## How To Use
To use the lib is very simple. First you need to add the service to the collection. To do so, use the ```services.AddHealthChecks()``` overload that contains the cache setup.
With the service in place, call ```.EnableCache()``` right after the check registration.

```csharp
    services.AddHealthChecks(options =>
        {
            options.DefaultCacheDuration = TimeSpan.FromMinutes(10);
            options.Tag = "cacheable";
            options.CacheExceptions = false;
            options.CachedExceptionsDuration = null;
        })
        .Add(registration).EnableCache();
```
In the example above, I used ```.Add()``` but the extension will work with any third party check library, like the awesome [AspNetCore.Diagnostics.HealthChecks](https://github.com/Xabaril/AspNetCore.Diagnostics.HealthChecks).

## AddHealthChecks Setup
When calling the ```services.AddHealthChecks()``` overload, you can provide some options to the service. The options are described below.

|Option|Description|Default|
|-|-|-|
|Tag|The tag used to identify cacheable registrations|```"cacheable"```|
|CachePrefix|The prefix to use when caching a registration report|```"cacheable-hc-"```|
|DefaultCacheDuration|The default cache duration|10 minutes|
|CacheExceptions|Indicate if checks that throw exceptions should be cached|```false```|
|CachedExceptionsDuration|When ```CacheExceptions``` is ```true```, the checks that throw exception will be cached using this duration. If none is provided, ```DefaultCacheDuration``` will be used.|```null```|

## EnableCache Setup
When calling ```.EnableCache()``` you can provide some options, but it is up to you. You can call it with no arguments and simply use the ones provided above.

|Option|Description|Default|
|-|-|-|
|CacheDuration|When provided, will override the  ```DefaultCacheDuration``` for a single registration.|```null```|
|ExceptionCacheDuration|When provided, will override the ```CachedExceptionsDuration``` for a single registration.|```null```|

## How Does The Lib Work
The lib replaces the ```DefaultHealthCheckService``` provided by Microsoft and inject its own, called ```CacheableHealthCheckService```.
This new service was largely based on the default implementation, so you can expect the exact same behavior, with one exception, you can now cache responses.

When you call ```.EnableCache()``` after a registration, the lib adds a tag `cacheable` to the list of tags for it. You can change this tag using the options if you think you may collide with this one.

When the health check enpoint is called, the lib invokes the predicate you registered when you called ```app.UseHealthChecks()```, so it takes precedence over cached responses.

When the service finds a registration with the specific tag, it adds a message to its data dictionary and add it to the cache. Next time the endpoint is called, the cached response will be reported and the health check will not be run until it expires.

The cache is done using an instance of the ```IMemoryCache``` and the key to the cache is built using the ```CachePrefix``` and registration name.

Note: Only registrations with the specific tag will be cached. This means that you can have health checks that don't get cached side by side with one that is and one will not affect the other.

## Want to contribute
If you find bugs and don't have time to submit a PR, please report it using the github isses.
On the other hand, if you can submit a PR I'll gladly take a look at it.

