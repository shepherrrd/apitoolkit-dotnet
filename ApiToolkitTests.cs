using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ApiToolkit.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Text;
using Newtonsoft.Json;
using System.Diagnostics;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;
using Moq;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
public class RedactTests
{
    [Test]
    public void TestRedact_Success()
    {
        // Arrange
        string jsonString = @"
            {
                'name': 'John Doe',
                'addresses': [{
                    'street': '123 Main St',
                    'city': {'name':'Anytown'},
                    'state': 'CA',
                    'house_no': {'number':'5A', 'floor': 'floor'},
                    'zip': ['12345', '0987']
                }],
                'phone': '555-555-1212',
                'email': 'jdoe@example.com'
            }";
        List<string> jsonPaths = new List<string>
        {
            "$.name",
            "$.addresses[*].street",
            "$.addresses[*].city.name",
            "$.phone",
            "$.addresses[*].house_no",
            "$.addresses[*].zip[*]",
            "$.invalid.path" // NotFound paths should not affect final json
        };

        // Act
        byte[] result = Client.RedactJSON(Encoding.UTF8.GetBytes(jsonString), jsonPaths);

        // Assert
        JObject expectedObject = JObject.Parse(@"
            {
                'name': '[CLIENT_REDACTED]',
                'addresses': [{
                    'street': '[CLIENT_REDACTED]',
                    'city': {'name': '[CLIENT_REDACTED]'},
                    'state': 'CA',
                    'house_no': '[CLIENT_REDACTED]',
                    'zip': ['[CLIENT_REDACTED]', '[CLIENT_REDACTED]']
                }],
                'phone': '[CLIENT_REDACTED]',
                'email': 'jdoe@example.com'
            }");

        JObject resultObject = JObject.Parse(Encoding.UTF8.GetString(result));
        Assert.IsTrue(JToken.DeepEquals(expectedObject, resultObject));
    }

    [Test]
    public void TestRedactHeaders()
    {
        // Define test data
        var headers = new Dictionary<string, List<string>>
        {
            { "Content-Type", new List<string> { "application/json" } },
            { "Authorization", new List<string> { "Bearer abcdefg" } },
            { "X-Forwarded-For", new List<string> { "127.0.0.1" } }
        };
        var redactList = new List<string> { "authorization", "x-forwarded-for", "invalid-one" };

        // Test success case
        var expected = new Dictionary<string, List<string>>
        {
            { "Content-Type", new List<string> { "application/json" } },
            { "Authorization", new List<string> { "[CLIENT_REDACTED]" } },
            { "X-Forwarded-For", new List<string> { "[CLIENT_REDACTED]" } }
        };
        var actual = Client.RedactHeaders(headers, redactList);
        Assert.That(actual, Is.EqualTo(expected));

        // Test failure case
        redactList = null;
        expected = headers;
        actual = Client.RedactHeaders(headers, redactList);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void TestBuildPayload()
    {
        // Arrange
        var sdkType = "test-sdk";
        // Create a new HttpContext with custom request headers and query parameters
        var req = new DefaultHttpContext().Request;
        req.Method = "POST";
        req.Host = new HostString("example.com");
        req.Path = "/path/to/resource";
        req.Headers.Add("Authorization", "Value");
        req.Query = new QueryCollection(new Dictionary<string, StringValues>
      {
          { "param1", "value1" },
          { "param2", "value2" }
      });

        var statusCode = 200;
        var reqBody = Encoding.UTF8.GetBytes("{\"foo\":\"bar\"}");
        var respBody = Encoding.UTF8.GetBytes("{\"baz\":42}");
        var respHeaders = new Dictionary<string, List<string>>
      {
          { "Content-Type", new List<string> { "application/json" } }
      };
        var pathParams = new Dictionary<string, string>
      {
          { "id", "123" }
      };
        var urlPath = "/path/to/resource/{id}";
        var config = new Config
        {
            RedactHeaders = new List<string> { "Authorization" },
            RedactRequestBody = new List<string> { "$.foo" },
            Debug = true,
            VerboseDebug = true
        };

        Stopwatch stopwatch = new Stopwatch();
        stopwatch.Start();

        var metadata = new ClientMetadata
        {
            ProjectId = "project_id"
        };

        var client = new Client(null, null, config, metadata);

        // Act
        var payload = client.BuildPayload(sdkType, stopwatch, req, statusCode, reqBody, respBody, respHeaders, pathParams, urlPath, new List<ATError> { }, "");

        // Assert
        Assert.AreEqual(req.Host.Host, payload.Host);
        Assert.AreEqual(req.Method, payload.Method);
        Assert.AreEqual(new Dictionary<string, string>
      {
          { "id", "123" },
      }, payload.PathParams);
        Assert.AreEqual(1, payload.ProtoMajor);
        Assert.AreEqual(1, payload.ProtoMinor);
        Assert.AreEqual(new Dictionary<string, List<string>>
      {
          { "param1", new List<string> { "value1" } },
          { "param2", new List<string> { "value2" } }
      }, payload.QueryParams);
        Assert.AreEqual(req.Path + req.QueryString, payload.RawUrl);
        Assert.AreEqual(req.Headers["Referer"].ToString(), payload.Referer);
        Assert.AreEqual("{\n  \"foo\": \"[CLIENT_REDACTED]\"\n}", Encoding.UTF8.GetString(payload.RequestBody));
        Assert.AreEqual("[CLIENT_REDACTED]", payload.RequestHeaders["Authorization"][0]);
        Assert.AreEqual("{\"baz\":42}", Encoding.UTF8.GetString(payload.ResponseBody));
        Assert.AreEqual("application/json", payload.ResponseHeaders["Content-Type"][0]);
        Assert.AreEqual(sdkType, payload.SdkType);
        Assert.AreEqual(statusCode, payload.StatusCode);
        Assert.AreEqual("POST", payload.Method);
        Assert.GreaterOrEqual(DateTime.UtcNow, payload.Timestamp);
        Assert.AreEqual(urlPath, payload.UrlPath);
        Console.WriteLine($"APIToolkit: {JsonConvert.SerializeObject(payload)}");
    }

    // [Test]
    public async Task MiddlewareTest_ReturnsNotFoundForRequest()
    {
        var config = new Config
        {
            Debug = true,
            ApiKey = ""
        };
        var client = await APIToolkit.NewClientAsync(config);
        var host = await new HostBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .Configure(app =>
                    {
                        app.Use(async (context, next) =>
                        {
                            var apiToolkit = new ApiToolkit.Net.APIToolkit(next, client);
                            await apiToolkit.InvokeAsync(context);
                        });
                        // app.Use(async (context, next) =>
                        // {
                        //     var input = await new StreamReader(context.Request.Body).ReadToEndAsync();
                        //     context.Response.ContentType = "application/json";
                        //     await context.Response.WriteAsync(input);
                        // });
                    });
            })
            .StartAsync();

        using var c = host.GetTestClient();
        var body = new { Property1 = "Value1", Property2 = "Value2" };
        var response = await c.PostAsync("/", new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"));
        Console.WriteLine($"🔥 parent apitoolkit metadata in test {response}");
    }
}


    public class ApiToolkitClientFactoryTests
    {
        private Mock<IHttpClientFactory> _httpClientFactoryMock;
        private Mock<IServiceProvider> _serviceProviderMock;
        private Mock<ObservingHandler> _observingHandlerMock;
        private IApiToolkitClientFactory _apiToolkitClientFactory;

        [SetUp]
        public void SetUp()
        {
            _httpClientFactoryMock = new Mock<IHttpClientFactory>();
            _serviceProviderMock = new Mock<IServiceProvider>();
            _observingHandlerMock = new Mock<ObservingHandler>();

            // Set up the service provider to return the observing handler mock
            _serviceProviderMock
                .Setup(sp => sp.GetService(typeof(ObservingHandler)))
                .Returns(_observingHandlerMock.Object);

            // Initialize the ApiToolkitClientFactory with mocks
            _apiToolkitClientFactory = new ApiToolkitClientFactory(_httpClientFactoryMock.Object, _serviceProviderMock.Object);
        }

        [Test]
        public void CreateClient_ShouldReturnHttpClient_WithConfiguredHandler()
        {
            // Arrange
            var options = new ATOptions
            {
                PathWildCard = "/posts/{id}",
                RedactHeaders = new List<string> { "User-Agent" },
                RedactRequestBody =  new List<string> { "$.user.password" },
                RedactResponseBody =  new List<string> { "$.user.data.email" }
            };

            // Act
            var client = _apiToolkitClientFactory.CreateClient(options);

            // Assert
            Assert.NotNull(client);
            _observingHandlerMock.Verify(h => h.SetOptions(options), Times.Once);
        }

        [Test]
        public void CreateClient_ShouldUseObservingHandler_FromServiceProvider()
        {
            // Act
            var client = _apiToolkitClientFactory.CreateClient(new ATOptions());

            // Assert
            _serviceProviderMock.Verify(sp => sp.GetService(typeof(ObservingHandler)), Times.Once);
        }

        [Test]
        public void CreateClient_ShouldThrowException_WhenHandlerNotRegistered()
        {
            // Arrange
            var invalidServiceProvider = new Mock<IServiceProvider>();
            invalidServiceProvider.Setup(sp => sp.GetService(typeof(ObservingHandler)))
                                  .Returns(null); // Simulate missing handler registration

            var factory = new ApiToolkitClientFactory(_httpClientFactoryMock.Object, invalidServiceProvider.Object);

            // Act & Assert
            Assert.Throws<NullReferenceException>(() => factory.CreateClient(new ATOptions()));
        }
    }



