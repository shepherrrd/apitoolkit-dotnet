﻿using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Google.Protobuf.WellKnownTypes;
using Google.Protobuf;
using Microsoft.AspNetCore.Http.Extensions;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;



namespace ApiToolkit.Net
{
  public class APIToolkit
  {
    private readonly RequestDelegate _next;
    private readonly Client _client;

    public APIToolkit(RequestDelegate next, Client client)
    {
      _next = next;
      _client = client;
    }

    public async Task InvokeAsync(HttpContext context)
    {
      Stopwatch stopwatch = new Stopwatch();
      stopwatch.Start();
      context.Request.EnableBuffering(); // so we can read the body stream multiple times

      var responseBodyStream = new MemoryStream();
      var originalResponseBodyStream = context.Response.Body;
      context.Response.Body = responseBodyStream;

      try
      {
        await _next(context); // execute the next middleware in the pipeline
      }
      finally
      {
        var requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
        context.Request.Body.Position = 0; // reset the body stream to the beginning

        responseBodyStream.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(responseBodyStream).ReadToEndAsync();
        responseBodyStream.Seek(0, SeekOrigin.Begin);

        await responseBodyStream.CopyToAsync(originalResponseBodyStream);
        context.Response.Body = originalResponseBodyStream;

        var pathParams = context.GetRouteData().Values
            .Where(v => !string.IsNullOrEmpty(v.Value?.ToString()))
            .ToDictionary(v => v.Key, v => v.Value.ToString());
        var urlPath = "";
        var endpoint = context.GetEndpoint();
        if (endpoint != null)
        {
          var routePattern = (endpoint as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern?.RawText;

          if (routePattern != null)
          {
            urlPath = routePattern;
          }
        }


        var responseHeaders = context.Response.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
        var errors = new List<ATError>();
        if (context.Items.TryGetValue("APITOOLKIT_ERRORS", out var errorListObj) && errorListObj is List<ATError> errorList)
        {
          errors = (List<ATError>)errorListObj;
        }
        var payload = _client.BuildPayload("DotNet", stopwatch, context.Request, context.Response.StatusCode,
            System.Text.Encoding.UTF8.GetBytes(requestBody), System.Text.Encoding.UTF8.GetBytes(responseBody),
            responseHeaders, pathParams, urlPath, errors);

        await _client.PublishMessageAsync(payload);
      }
    }

    public static async Task<Client> NewClientAsync(Config cfg)
    {
      var url = "https://app.apitoolkit.io";
      if (!string.IsNullOrEmpty(cfg.RootUrl))
      {
        url = cfg.RootUrl;
      }

      using HttpResponseMessage response = await new HttpClient
      {
        DefaultRequestHeaders = {
          {
        "Authorization", $"Bearer {cfg.ApiKey}"
          }
        }
      }.GetAsync($"{url}/api/client_metadata");
      if (!response.IsSuccessStatusCode)
      {
        throw new Exception($"APIToolkit: Unable to query apitoolkit for client metadata: {response.StatusCode}");
      }

      var clientMetadata = JsonConvert.DeserializeObject<ClientMetadata>(await response.Content.ReadAsStringAsync());
      if (clientMetadata is null)
      {
        throw new Exception("APIToolkit: Unable to deserialize client metadata response");
      }

      var credentials = GoogleCredential
          .FromJson(clientMetadata.PubsubPushServiceAccount.ToString())
          .CreateScoped(PublisherServiceApiClient.DefaultScopes);

      var publisher = new PublisherClientBuilder();
      publisher.Credential = credentials;
      publisher.TopicName = new TopicName(clientMetadata.PubsubProjectId, clientMetadata.TopicId);
      var pubsubClient = publisher.Build();
      var client = new Client(pubsubClient, null, cfg, clientMetadata);
      if (client.Config.Debug)
      {
        Console.WriteLine("APIToolkit: client initialized successfully");
      }
      return client;
    }
  }


  public class Client
  {
    public readonly PublisherClient PubSubClient;
    public readonly TopicName TopicName;
    public readonly Config Config;
    public readonly ClientMetadata Metadata;

    public Client(PublisherClient pubSubClient, TopicName topicName, Config config, ClientMetadata metadata)
    {
      PubSubClient = pubSubClient;
      TopicName = topicName;
      Config = config;
      Metadata = metadata;
    }

    public async Task PublishMessageAsync(Payload payload)
    {
      if (PubSubClient == null)
      {
        if (Config.Debug)
        {
          Console.WriteLine("APIToolkit: topic is not initialized. Check client initialization. Messages are not being sent to apitoolkit");
        }
        return;
      }

      await PubSubClient.PublishAsync(new PubsubMessage
      {
        Data = ByteString.CopyFromUtf8(JsonConvert.SerializeObject(payload)),
        PublishTime = Timestamp.FromDateTime(DateTime.UtcNow),
      });

      if (Config.Debug)
      {
        Console.WriteLine("APIToolkit: message published to pubsub topic");

        if (Config.VerboseDebug)
        {
          Console.WriteLine($"APIToolkit: {JsonConvert.SerializeObject(payload)}");
        }
      }
    }

    public ObservingHandler APIToolkitObservingHandler(HttpContext context)
    {
      return new ObservingHandler(context, PublishMessageAsync, BuildPayload);
    }


    public void ReportError(HttpContext context, Exception error)
    {
      var atError = BuildError(error);

      if (context.Items.TryGetValue("APITOOLKIT_ERRORS", out var errorListObj) && errorListObj is List<ATError> errorList)
      {
        errorList.Add(atError);
      }
      else
      {
        errorList = new List<ATError> { atError };
        context.Items["APITOOLKIT_ERRORS"] = errorList;
      }
    }

    public Payload BuildPayload(string SDKType, Stopwatch stopwatch, HttpRequest req, int statusCode, byte[] reqBody, byte[] respBody, Dictionary<string, List<string>> respHeader, Dictionary<string, string> pathParams, string urlPath, List<ATError> errors)
    {
      if (req == null || Metadata is null)
      {
        // Early return with empty payload to prevent any null reference exceptions
        if (Config.Debug)
        {
          Console.WriteLine("APIToolkit: null request or client or url while building payload.");
        }
        return new Payload();
      }
      string projectId = Metadata is null ? "" : Metadata.ProjectId;

      var reqHeaders = req.Headers.ToDictionary(h => h.Key, h => h.Value.ToList());
      int[] versionParts = req.Protocol.Split('/', '.').Skip(1).Select(int.Parse).ToArray();
      var (majorVersion, minorVersion) = versionParts.Length >= 2 ? (versionParts[0], versionParts[1]) : (1, 1);

      stopwatch.Stop();
      return new Payload
      {
        Duration = stopwatch.ElapsedTicks * 100,
        Host = req.Host.Host,
        Method = req.Method,
        PathParams = pathParams,
        ProjectId = projectId,
        ProtoMajor = majorVersion,
        ProtoMinor = minorVersion,
        QueryParams = req.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        RawUrl = req.GetEncodedPathAndQuery(),
        Referer = req.Headers["Referer"].ToString(),
        RequestBody = RedactJSON(reqBody, Config.RedactRequestBody),
        RequestHeaders = RedactHeaders(reqHeaders, Config.RedactHeaders),
        ResponseBody = RedactJSON(respBody, Config.RedactResponseBody),
        ResponseHeaders = RedactHeaders(respHeader, Config.RedactHeaders),
        SdkType = SDKType,
        StatusCode = statusCode,
        Timestamp = DateTime.UtcNow,
        UrlPath = urlPath,
        Errors = errors
      };
    }




    private ATError BuildError(Exception error)
    {
      // Create an instance of ATError
      var atError = new ATError
      {
        When = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ErrorType = error.GetType().Name,
        Message = error.Message,
        StackTrace = error.StackTrace ?? ""
      };

      // Try to obtain RootErrorType and RootErrorMessage from inner exceptions
      var innerException = error;
      while (innerException.InnerException != null)
      {
        innerException = innerException.InnerException;
      }

      atError.RootErrorType = innerException.GetType().Name;
      atError.RootErrorMessage = innerException.Message;

      return atError;
    }
    public static byte[] RedactJSON(byte[] data, List<string> jsonPaths)
    {
      if (jsonPaths is null || jsonPaths.Count == 0 || !data.Any()) return data;

      try
      {
        JObject jsonObject = JObject.Parse(System.Text.Encoding.UTF8.GetString(data));
        (jsonPaths ?? new List<string>()).ForEach(jPath => jsonObject.SelectTokens(jPath).ToList().ForEach(token => token.Replace("[CLIENT_REDACTED]")));
        return System.Text.Encoding.UTF8.GetBytes(jsonObject.ToString());
      }
      catch (Exception)
      {
        return data;
      }
    }


    public static Dictionary<string, List<string>> RedactHeaders(Dictionary<string, List<string>> headers, List<string> redactList)
    {
      redactList = (redactList ?? new List<string>()).Select(s => s.ToLower()).ToList();
      return headers
          .ToDictionary(
              kvp => redactList.Contains(kvp.Key.ToLower()) ? kvp.Key : kvp.Key,
              kvp => redactList.Contains(kvp.Key.ToLower()) ? new List<string> { "[CLIENT_REDACTED]" } : kvp.Value
          );
    }
  }


  public class ClientMetadata
  {
    [JsonProperty("project_id")]
    public string ProjectId { get; set; }

    [JsonProperty("pubsub_project_id")]
    public string PubsubProjectId { get; set; }

    [JsonProperty("topic_id")]
    public string TopicId { get; set; }

    [JsonProperty("pubsub_push_service_account")]
    public JRaw PubsubPushServiceAccount { get; set; }
  }

  public class Config
  {
    public bool Debug { get; set; }
    public bool VerboseDebug { get; set; }
    public string RootUrl { get; set; }
    public string ApiKey { get; set; }
    public List<string> RedactHeaders { get; set; }
    public List<string> RedactRequestBody { get; set; }
    public List<string> RedactResponseBody { get; set; }
  }

  public class Payload
  {
    [JsonProperty("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonProperty("request_headers")]
    public Dictionary<string, List<string>> RequestHeaders { get; set; }

    [JsonProperty("query_params")]
    public Dictionary<string, List<string>> QueryParams { get; set; }

    [JsonProperty("path_params")]
    public Dictionary<string, string> PathParams { get; set; }

    [JsonProperty("response_headers")]
    public Dictionary<string, List<string>> ResponseHeaders { get; set; }

    [JsonProperty("method")]
    public string Method { get; set; }

    [JsonProperty("sdk_type")]
    public string SdkType { get; set; }

    [JsonProperty("host")]
    public string Host { get; set; }

    [JsonProperty("raw_url")]
    public string RawUrl { get; set; }

    [JsonProperty("referer")]
    public string Referer { get; set; }

    [JsonProperty("project_id")]
    public string ProjectId { get; set; }

    [JsonProperty("url_path")]
    public string UrlPath { get; set; }

    [JsonProperty("response_body")]
    public byte[] ResponseBody { get; set; }

    [JsonProperty("request_body")]
    public byte[] RequestBody { get; set; }

    [JsonProperty("proto_minor")]
    public int ProtoMinor { get; set; }

    [JsonProperty("status_code")]
    public int StatusCode { get; set; }

    [JsonProperty("proto_major")]
    public int ProtoMajor { get; set; }

    //Nanoseconds
    [JsonProperty("duration")]
    public long Duration { get; set; }
    [JsonProperty("errors")]
    public List<ATError>? Errors { get; set; }
  }


  public class ATError
  {

    [JsonProperty("when")]
    public string When { get; set; }
    [JsonProperty("error_type")]
    public string ErrorType { get; set; }
    [JsonProperty("root_error_type")]
    public string RootErrorType { get; set; }
    [JsonProperty("message")]
    public string Message { get; set; }
    [JsonProperty("root_error_message")]

    public string RootErrorMessage { get; set; }
    [JsonProperty("stack_trace")]
    public string StackTrace { get; set; }
  }

  public class ObservingHandler : DelegatingHandler
  {
    private readonly HttpContext _context;
    private readonly Func<Payload, Task> _publishMessageAsync;
    private readonly Func<string, Stopwatch, HttpRequest, int, byte[], byte[], Dictionary<string, List<string>>, Dictionary<string, string>, string, List<ATError>, Payload> _buildPayload;
    public ObservingHandler(HttpContext httpContext, Func<Payload, Task> publishMessage, Func<string, Stopwatch, HttpRequest, int, byte[], byte[], Dictionary<string, List<string>>, Dictionary<string, string>, string, List<ATError>, Payload> buildPayload)
    {
      _context = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
      _publishMessageAsync = publishMessage;
      _buildPayload = buildPayload;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      var requestInfo = new
      {
        StartTime = DateTimeOffset.UtcNow,
        Method = request.Method,
        RequestUri = request.RequestUri,
        Headers = request.Headers,
        Body = await request.Content.ReadAsStringAsync(),
      };

      var response = await base.SendAsync(request, cancellationToken);

      var responseInfo = new
      {
        Duration = DateTimeOffset.UtcNow - requestInfo.StartTime,
        StatusCode = response.StatusCode,
        Headers = response.Headers,
        Body = await response.Content.ReadAsStringAsync(),
      };


      return response;
    }
  }

}