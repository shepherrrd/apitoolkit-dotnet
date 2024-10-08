<div align="center">

![APItoolkit's Logo](https://github.com/apitoolkit/.github/blob/main/images/logo-white.svg?raw=true#gh-dark-mode-only)
![APItoolkit's Logo](https://github.com/apitoolkit/.github/blob/main/images/logo-black.svg?raw=true#gh-light-mode-only)

## .NET Core SDK

[![APItoolkit SDK](https://img.shields.io/badge/APItoolkit-SDK-0068ff?logo=dotnet)](https://github.com/topics/apitoolkit-sdk) [![Join Discord Server](https://img.shields.io/badge/Chat-Discord-7289da)](https://apitoolkit.io/discord?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme) [![APItoolkit Docs](https://img.shields.io/badge/Read-Docs-0068ff)](https://apitoolkit.io/docs/sdks/dotnet/dotnetcore?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme) [![Build Status](https://github.com/apitoolkit/apitoolkit-dotnet/workflows/.NET/badge.svg)](https://github.com/apitoolkit/apitoolkit-dotnet1/actions?query=workflow%3ACI) [![NuGet](https://img.shields.io/nuget/v/ApiToolkit.Net.svg)](https://nuget.org/packages/ApiToolkit.Net) [![Nuget](https://img.shields.io/nuget/dt/ApiToolkit.Net.svg)](https://nuget.org/packages/ApiToolkit.Net)

APItoolkit is an end-to-end API and web services management toolkit for engineers and customer support teams. To integrate .Net web services with APItoolkit, you need to use this SDK to monitor incoming traffic, aggregate the requests, and then deliver them to the APItoolkit's servers.

</div>

---

## Table of Contents

- [Installation](#installation)
- [Configuration](#configuration)
- [Contributing and Help](#contributing-and-help)
- [License](#license)

---

## Installation

Kindly run the command below to install the package:

```sh
dotnet add package ApiToolkit.Net
```

## Configuration

Next, initialize APItoolkit in your application's entry point (e.g., `Program.cs`) like so:

```csharp
using ApiToolkit.Net;

// Initialize the APItoolkit client
builder.Services.AddTransient<ObservingHandler>();

// Register the custom API Toolkit Client Factory
builder.Services.AddSingleton<IApiToolkitClientFactory, ApiToolkitClientFactory>();

var config = new Config
{
    ApiKey = "{ENTER_YOUR_API_KEY_HERE}",
    Debug = false,
    Tags = new List<string> { "environment: production", "region: us-east-1" },
    ServiceVersion: "v2.0",
};
var client = await APIToolkit.NewClientAsync(config);
// END Initialize the APItoolkit client

# Register the middleware to use the initialized client
app.Use(async (context, next) =>
{
    var apiToolkit = new APIToolkit(next, client);
    await apiToolkit.InvokeAsync(context);
});

# app.UseEndpoint(..)
# other middleware and logic
# ...
```

## Usage

You can now use the IApiToolKitClientFactory Interface to directly make your Http requests

```csharp
public class MyService
{
    private readonly IApiToolkitClientFactory _apiToolkitClientFactory;

    public MyService(IApiToolkitClientFactory apiToolkitClientFactory)
    {
        _apiToolkitClientFactory = apiToolkitClientFactory;
    }

    public async Task<string> GetPostAsync()
    {
        var options = new ATOptions
        {
            PathWildCard = "/posts/{id}",
            RedactHeaders = new[] { "User-Agent" },
            RedactRequestBody = new[] { "$.user.password" },
            RedactResponseBody = new[] { "$.user.data.email" }
        };

        var client = _apiToolkitClientFactory.CreateClient(options);
        var response = await client.GetAsync("https://jsonplaceholder.typicode.com/posts/1");
        return await response.Content.ReadAsStringAsync();
    }
}
```

Traditional Middleware Setup
If you prefer to set up the middleware traditionally, here's how you can initialize APItoolkit in your application's entry point (e.g., Program.cs):

```csharp
using ApiToolkit.Net;

// Initialize the APItoolkit client
var config = new Config
{
    ApiKey = "{ENTER_YOUR_API_KEY_HERE}",
    Debug = false,
    Tags = new List<string> { "environment: production", "region: us-east-1" },
    ServiceVersion: "v2.0",
};
var client = await APIToolkit.NewClientAsync(config);

// Register the middleware to use the initialized client
app.Use(async (context, next) =>
{
    var apiToolkit = new APIToolkit(next, client);
    await apiToolkit.InvokeAsync(context);
});

// app.UseEndpoint(..)
// other middleware and logic
// ...
```


> [!NOTE]
> 
> - Please make sure the APItoolkit middleware is added before `UseEndpoint` and other middleware are initialized. 
> - The `{ENTER_YOUR_API_KEY_HERE}` demo string should be replaced with the [API key](https://apitoolkit.io/docs/dashboard/settings-pages/api-keys?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme) generated from the APItoolkit dashboard.

<br />

> [!IMPORTANT]
> 
> To learn more configuration options (redacting fields, error reporting, outgoing requests, etc.), please read this [SDK documentation](https://apitoolkit.io/docs/sdks/dotnet/dotnetcore?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme).

## Contributing and Help

To contribute to the development of this SDK or request help from the community and our team, kindly do any of the following:
- Read our [Contributors Guide](https://github.com/apitoolkit/.github/blob/main/CONTRIBUTING.md).
- Join our community [Discord Server](https://apitoolkit.io/discord?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme).
- Create a [new issue](https://github.com/apitoolkit/apitoolkit-dotnet/issues/new/choose) in this repository.

## License

This repository is published under the [MIT](LICENSE) license.

---

<div align="center">
    
<a href="https://apitoolkit.io?utm_campaign=devrel&utm_medium=github&utm_source=sdks_readme" target="_blank" rel="noopener noreferrer"><img src="https://github.com/apitoolkit/.github/blob/main/images/icon.png?raw=true" width="40" /></a>

</div>
