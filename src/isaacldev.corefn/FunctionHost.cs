using Azure.Data.Tables;
using FishyFlip;
using FishyFlip.Models;
using isaacldev.domain;
using Mastonet;
using Mastonet.Entities;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Debug;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Tweetinvi;
using Tweetinvi.Core.Web;

namespace isaacldev.corefn
{
    public class FunctionHost
    {
        private readonly TelemetryClient telemetryClient = new TelemetryClient();

        /// Using dependency injection will guarantee that you use the same configuration for telemetry collected automatically and manually.
        //        public FunctionHost(TelemetryConfiguration telemetryConfiguration)
        //        {
        //#if !DEBUG
        //            telemetryConfiguration.InstrumentationKey = Environment.GetEnvironmentVariable("APPINSIGHTS_INSTRUMENTATIONKEY");
        //            telemetryClient = new TelemetryClient(telemetryConfiguration);
        //#endif
        //        }

        // this is redirect target when the short URL isn't found
        public readonly string FallbackUrl = Environment.GetEnvironmentVariable(Utility.ENV_FALLBACK) ??
            "https://www.isaaclevin.com/?utm_source=isaacl&utm_medium=redirect&utm_campaign=isaacl_dev";

        // for tagging, the "utm_source" or source part of WebTrends tag 
        public readonly string Source = Environment.GetEnvironmentVariable(Utility.ENV_SOURCE) ??
            "isaacl";

        public readonly string ShortenerBase = Environment.GetEnvironmentVariable(Utility.SHORTENER_BASE) ??
            "http://isaacl.dev/";

        public readonly string TwitterConsumerKey = Environment.GetEnvironmentVariable(Utility.TWITTER_CONSUMER_KEY);

        public readonly string TwitterConsumerSecret = Environment.GetEnvironmentVariable(Utility.TWITTER_CONSUMER_SECRET) ??
            "";

        public readonly string TwitterAccessToken = Environment.GetEnvironmentVariable(Utility.TWITTER_ACCESS_TOKEN) ??
            "";

        public readonly string TwitterAccessSecret = Environment.GetEnvironmentVariable(Utility.TWITTER_ACCESS_SECRET) ??
            "";

        public readonly string MastodonAccessToken = Environment.GetEnvironmentVariable(Utility.MASTODON_ACCESS_TOKEN) ??
    "";



        // default campaign, for tagging 
        public readonly string DefaultCampaign = Environment.GetEnvironmentVariable(Utility.ENV_CAMPAIGN) ??
            "link";

        private async Task TrackDependencyAsync(
            string dependency,
            string command,
            Func<Task> commandAsync,
            Func<bool> success)
        {
#if DEBUG
            await commandAsync();
            return;
#else
            var startTime = DateTime.UtcNow;
            var timer = System.Diagnostics.Stopwatch.StartNew();

            await commandAsync();

            telemetryClient.TrackDependency(dependency, command, startTime, timer.Elapsed, success());
#endif
        }

        private HttpResponseMessage SecurityCheck(HttpRequestMessage req)
        {
            return req.IsLocal() || req.RequestUri.Scheme == "https" ? null :
                req.CreateResponse(HttpStatusCode.Forbidden);
        }

        // returns a single page application to build links
        [FunctionName("Utility")]
        public HttpResponseMessage Admin([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestMessage req,
            ILogger log)
        {
            const string PATH = "LinkShortener.html";

            var result = SecurityCheck(req);
            if (result != null)
            {
                return result;
            }

            var scriptPath = Path.Combine(Environment.CurrentDirectory, "www");
            if (!Directory.Exists(scriptPath))
            {
                scriptPath = Path.Combine(
                    Environment.GetEnvironmentVariable("HOME", EnvironmentVariableTarget.Process),
                    @"site\wwwroot\www");
            }
            var filePath = Path.GetFullPath(Path.Combine(scriptPath, PATH));
            if (!File.Exists(filePath))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            log.LogInformation($"Attempting to retrieve file at path {filePath}.");
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            var stream = new FileStream(filePath, FileMode.Open);
            response.Content = new StreamContent(stream);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return response;
        }

        [FunctionName("ShortenUrl")]
        public async Task<IActionResult> ShortenUrl(
       [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestMessage req,
       [Table(Utility.TABLE, "1", Utility.KEY, Take = 1)] NextId keyTable,
       ILogger log)
        {
            log.LogInformation($"C# triggered function called with req: {req}");

            if (req == null)
            {
                return new NotFoundResult();
            }

            var check = SecurityCheck(req);
            if (check != null)
            {
                new ObjectResult(check);
            }

            ShortRequest input = await req.Content.ReadAsAsync<ShortRequest>();

            if (input == null)
            {
                return new NotFoundResult();
            }

            try
            {
                var result = new List<ShortResponse>();
                var analytics = new Analytics();

                // determine whether or not to process analytics tags
                bool tagMediums = analytics.Validate(input);

                var campaign = string.IsNullOrWhiteSpace(input.Campaign) ? DefaultCampaign : input.Campaign;
                var url = input.Input.Trim();
                var utm = analytics.TagUtm(input);
                var wt = analytics.TagWt(input);

                TableServiceClient tableServiceClient = new TableServiceClient(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));

                TableClient tableOut = tableServiceClient.GetTableClient(
                    tableName: Utility.TABLE
                    );

                await tableOut.CreateIfNotExistsAsync();

                if (keyTable == null)
                {
                    keyTable = new NextId
                    {
                        PartitionKey = "1",
                        RowKey = "KEY",
                        Id = 1024
                    };
                    await tableOut.AddEntityAsync<NextId>(keyTable);
                }

                log.LogInformation($"URL: {url} Tag UTM? {utm} Tag WebTrends? {wt}");
                log.LogInformation($"Current key: {keyTable?.Id}");

                // get host for building short URL 
                var host = req.RequestUri.GetLeftPart(UriPartial.Authority);

                // strategy for getting a new code 
                string getCode() => Utility.Encode(keyTable.Id++, input.ShortCode);

                // strategy for logging 
                void logFn(string msg) => log.LogInformation(msg);

                // strategy to save the key 
                async Task saveKeyAsync()
                {
                    await tableOut.UpdateEntityAsync(keyTable, Azure.ETag.All, TableUpdateMode.Replace);
                }
                // strategy to insert the new short url entry
                async Task saveEntryAsync(ITableEntity entry)
                {
                    var operationResult = await tableOut.AddEntityAsync(entry);
                }

                // strategy to create a new URL and track the dependencies
                async Task saveWithTelemetryAsync(ITableEntity entry)
                {
                    await TrackDependencyAsync(
                        "AzureTableStorageInsert",
                        "Insert",
                        async () => await saveEntryAsync(entry),
                        () => true);

                    //await TrackDependencyAsync(
                    //    "AzureTableStorageUpdate",
                    //    "Update",
                    //    async () => await saveKeyAsync(),
                    //    () => true);
                }

                if (tagMediums)
                {
                    // this will result in multiple entries depending on the number of 
                    // mediums passed in 
                    result.AddRange(await analytics.BuildAsync(
                        input,
                        Source,
                        host,
                        getCode,
                        saveWithTelemetryAsync,
                        logFn,
                        HttpUtility.ParseQueryString));
                }
                else
                {
                    // no tagging, just pass-through the URL
                    result.Add(await Utility.SaveUrlAsync(
                        url,
                        null,
                        host,
                        "",
                        "",
                        getCode,
                        logFn,
                        saveWithTelemetryAsync));
                }

                log.LogInformation($"Done.");
                return new OkObjectResult(result);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "An unexpected error was encountered.");
                return new BadRequestObjectResult(ex);
            }
        }

        [FunctionName(name: "HomeRedirect")]
        public async Task<HttpResponseMessage> HomeRedirect([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "UrlRedirect/")]HttpRequestMessage req,
           ILogger log)
        {
            var redirectUrl = "https://www.isaaclevin.com/";

            var response = req.CreateResponse(HttpStatusCode.Redirect);
            response.Headers.Add("Location", redirectUrl);
            return response;
        }

        [FunctionName(name: "UrlRedirect")]
        public async Task<HttpResponseMessage> UrlRedirect([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "UrlRedirect/{shortUrl}")]HttpRequestMessage req,
        string shortUrl,
        [Queue(queueName: Utility.QUEUE)] IAsyncCollector<string> queue,
        ILogger log)
        {
            log.LogInformation($"C# HTTP trigger function processed a request for shortUrl {shortUrl}");

            shortUrl = shortUrl.ToLower();

            if (shortUrl == Utility.ROBOTS)
            {
                log.LogInformation("Request for robots.txt.");
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Content = new StringContent(Utility.ROBOT_RESPONSE,
                    System.Text.Encoding.UTF8,
                    "text/plain");
                return resp;
            }

            var redirectUrl = FallbackUrl;

            if (!String.IsNullOrWhiteSpace(shortUrl))
            {
                shortUrl = shortUrl.Trim().ToLower();

                var partitionKey = shortUrl.First().ToString();

                log.LogInformation($"Searching for partition key {partitionKey} and row {shortUrl}.");


                TableServiceClient tableServiceClient = new TableServiceClient(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));

                TableClient inputTable = tableServiceClient.GetTableClient(
                    tableName: Utility.TABLE
                    );

                bool exists = false;
                await foreach (var tbl in tableServiceClient.QueryAsync(t => t.Name == Utility.TABLE))
                {
                    exists = true;
                }

                if (!exists)
                {
                    await inputTable.CreateIfNotExistsAsync();
                }
                ShortUrl result = null;

                await TrackDependencyAsync("AzureTableStorage", "Retrieve", async () =>
                {
                    result = await inputTable.GetEntityAsync<ShortUrl>(
                        rowKey: shortUrl,
                        partitionKey: partitionKey
                        );
                },
                () => result != null && result != null);

                if (result is ShortUrl fullUrl)
                {
                    log.LogInformation($"Found it: {fullUrl.Url}");
                    redirectUrl = WebUtility.UrlDecode(fullUrl.Url);
                }
                var referrer = string.Empty;
                if (req.Headers.Referrer != null)
                {
                    log.LogInformation($"Referrer: {req.Headers.Referrer.ToString()}");
                    referrer = req.Headers.Referrer.ToString();
                }
                log.LogInformation($"User agent: {req.Headers.UserAgent.ToString()}");
                await queue.AddAsync($"{shortUrl}|{redirectUrl}|{DateTime.UtcNow}|{referrer}|{req.Headers.UserAgent.ToString().Replace('|', '^')}");
            }
            else
            {
                telemetryClient.TrackEvent("Bad Link, resorting to fallback.");
            }

            var res = req.CreateResponse(HttpStatusCode.Redirect);
            res.Headers.Add("Location", redirectUrl);
            return res;
        }

        [FunctionName("KeepAlive")]
        public void KeepAlive([TimerTrigger(scheduleExpression: "0 */4 * * * *")] TimerInfo myTimer, ILogger log)
        {
            log.LogInformation("Keep-Alive invoked.");
        }

        [FunctionName("ProcessQueue")]
        public void ProcessQueue([QueueTrigger(queueName: Utility.QUEUE)] string request,
            [CosmosDB(Utility.URL_TRACKING, Utility.URL_STATS, CreateIfNotExists = false, Connection = "CosmosDb")] out dynamic doc,
            ILogger log)
        {
            try
            {
                AnalyticsEntry parsed = Utility.ParseQueuePayload(request);
                var page = parsed.LongUrl.AsPage(HttpUtility.ParseQueryString);

                telemetryClient.TrackPageView(page);
                log.LogInformation($"Tracked page view {page}");

                var analytics = parsed.LongUrl.ExtractCampaignAndMedium(HttpUtility.ParseQueryString);
                var campaign = analytics.Item1;
                var medium = analytics.Item2;

                if (!string.IsNullOrWhiteSpace(medium))
                {
                    telemetryClient.TrackEvent(medium);
                    log.LogInformation($"Tracked custom event: {medium}");
                }

                // cosmos DB 
                var normalize = new[] { '/' };
                doc = new ExpandoObject();
                doc.id = Guid.NewGuid().ToString();
                doc.page = page.TrimEnd(normalize);
                if (!string.IsNullOrWhiteSpace(parsed.ShortUrl))
                {
                    doc.shortUrl = parsed.ShortUrl;
                }
                if (!string.IsNullOrWhiteSpace(campaign))
                {
                    doc.campaign = campaign;
                }
                if (parsed.Referrer != null)
                {
                    doc.referrerUrl = parsed.Referrer.AsPage(HttpUtility.ParseQueryString);
                    doc.referrerHost = parsed.Referrer.DnsSafeHost;
                }
                if (!string.IsNullOrWhiteSpace(parsed.Agent))
                {
                    doc.agent = parsed.Agent;
                    try
                    {
                        var parser = UAParser.Parser.GetDefault();
                        var client = parser.Parse(parsed.Agent);
                        {
                            var browser = client.UA.Family;
                            var version = client.UA.Major;
                            var browserVersion = $"{browser} {version}";
                            doc.browser = browser;
                            doc.browserVersion = version;
                            doc.browserWithVersion = browserVersion;
                        }
                        if (client.Device.IsSpider)
                        {
                            doc.crawler = 1;
                        }
                        if (parsed.Agent.ToLowerInvariant().Contains("mobile"))
                        {
                            doc.mobile = 1;
                            var manufacturer = client.Device.Brand;
                            doc.mobileManufacturer = manufacturer;
                            var model = client.Device.Model;
                            doc.mobileModel = model;
                            doc.mobileDevice = $"{manufacturer} {model}";
                        }
                        else
                        {
                            doc.desktop = 1;
                        }
                        if (!string.IsNullOrWhiteSpace(client.OS.Family))
                        {
                            doc.platform = client.OS.Family;
                            doc.platformVersion = client.OS.Major;
                            doc.platformWithVersion = $"{client.OS.Family} {client.OS.Major}";
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, $"Error parsing user agent [{parsed.Agent}]");
                    }
                }
                doc.count = 1;
                doc.timestamp = parsed.TimeStamp;
                doc.host = parsed.LongUrl.DnsSafeHost;
                if (!string.IsNullOrWhiteSpace(medium))
                {
                    ((IDictionary<string, object>)doc).Add(medium, 1);
                }
                log.LogInformation($"CosmosDB: {doc.id}|{doc.page}|{parsed.ShortUrl}|{campaign}|{medium}");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "An unexpected error occurred.");
                throw;
            }
        }

        [FunctionName(name: "UpdateTwitter")]
        public async Task<HttpResponseMessage> Twitter([HttpTrigger(AuthorizationLevel.Function, "post",
            Route = "UpdateTwitter/{id}")]HttpRequestMessage req,
            [CosmosDB(Utility.URL_TRACKING, Utility.URL_STATS, CreateIfNotExists = false, Connection = "CosmosDb", Id = "{id}")] dynamic doc,
            string id,
            ILogger log)
        {
            var result = SecurityCheck(req);
            if (result != null)
            {
                return result;
            }
            if (doc == null)
            {
                log.LogError($"Doc not found with id: {id}.");
                return req.CreateResponse(HttpStatusCode.NotFound);
            }
            var link = await req.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(link))
            {
                doc.referralTweet = link;
            }
            return req.CreateResponse(HttpStatusCode.OK);
        }

        [FunctionName("TweetScheduler")]
        public async Task<IActionResult> TweetScheduler(
    [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
    ILogger log)
        {
            string shortUrl = req.Query["shortUrl"];

            if (!String.IsNullOrWhiteSpace(shortUrl))
            {
                shortUrl = shortUrl.Trim().ToLower();

                var partitionKey = shortUrl.First().ToString();

                log.LogInformation($"Searching for partition key {partitionKey} and row {shortUrl}.");

                TableServiceClient tableServiceClient = new TableServiceClient(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));

                TableClient inputTable = tableServiceClient.GetTableClient(
                    tableName: Utility.TABLE
                    );

                bool exists = false;
                await foreach (var tbl in tableServiceClient.QueryAsync(t => t.Name == Utility.TABLE))
                {
                    exists = true;
                }

                if (!exists)
                {
                    await inputTable.CreateIfNotExistsAsync();
                }

                ShortUrl result = null;

                await TrackDependencyAsync("AzureTableStorage", "Retrieve", async () =>
                {
                    result = await inputTable.GetEntityAsync<ShortUrl>(
                        rowKey: shortUrl,
                        partitionKey: partitionKey
                        );
                },
                () => result != null && result != null);

                ShortUrl linkInfo = result as ShortUrl;

                if (linkInfo != null && !linkInfo.Posted)
                {
                    //await Tweet(linkInfo);

                    //await PostToBlueSky(linkInfo);
                }
            }
            return new OkObjectResult("");
        }

        private async Task Tweet(ShortUrl linkInfo)
        {

            if (!Debugger.IsAttached)
            {
                var client = new TwitterClient(
                    TwitterConsumerKey,
                    TwitterConsumerSecret,
                    TwitterAccessToken,
                    TwitterAccessSecret
                    );

                var poster = new TweetsV2Poster(client);

                ITwitterResult tweetResult = await poster.PostTweet(
                    new TweetV2PostRequest
                    {
                        Text = $"{linkInfo.Title} \n {linkInfo.Message} \n\n {ShortenerBase}{linkInfo.RowKey}"
                    }
                );

                if (tweetResult.Response.IsSuccessStatusCode == false)
                {
                    throw new Exception(
                        "Error when posting tweet: " + Environment.NewLine + tweetResult.Content
                    );
                }
            }
        }

        private async Task PublishToMastodon(ShortUrl linkInfo)
        {
            var client = new MastodonClient("fosstodon.org", this.MastodonAccessToken);

            if (!Debugger.IsAttached)
            {
                var status = await client.PublishStatus($"{linkInfo.Title} \n {linkInfo.Message} \n\n {ShortenerBase}{linkInfo.RowKey}", new Visibility?((Visibility)0), (string)null, (IEnumerable<string>)null, false, (string)null, new DateTime?(), (string)null, (PollParameters)null);
            }
        }

        private async Task PostToBlueSky(ShortUrl linkInfo)
        {
            var atProtocol = new ATProtocolBuilder()
                        .WithLogger(new DebugLoggerProvider().CreateLogger("FishyFlip"))
            .Build();

            var session = await atProtocol.AuthenticateWithPasswordAsync("isaaclevin.com", "is04aac!");

            if (session is null)
            {
                Console.WriteLine("Failed to authenticate.");
                return;
            }

            string postTemplate = $"{linkInfo.Title} \n {linkInfo.Message} \n\n {ShortenerBase}{linkInfo.RowKey}";


            HttpClient client = new HttpClient();
            var card = await client.GetFromJsonAsync<BlueSkyCard>(linkInfo.Url);

            Facet? facet = null;
            FishyFlip.Models.Image? image = null;

            if (card != null)
            {
                var uri = new Uri(card.image);

                var uriWithoutQuery = uri.GetLeftPart(UriPartial.Path);
                var fileExtension = Path.GetExtension(uriWithoutQuery);

                var fileName = card.title;

                var imageBytes = await client.GetByteArrayAsync(uri);
                await File.WriteAllBytesAsync(Path.Combine(Path.GetTempPath(), $"{fileName}.jpg"), imageBytes);

                var stream = File.OpenRead(Path.Combine(Path.GetTempPath(), $"{fileName}.jpg"));
                var content = new StreamContent(stream);
                content.Headers.ContentLength = stream.Length;

                content.Headers.ContentType = new MediaTypeHeaderValue("image/jpg");
                var blobResult = await atProtocol.Repo.UploadBlobAsync(content);

                await blobResult.SwitchAsync(
                    async success =>
                    {
                        image = success.Blob.ToImage();

                        int promptStart = postTemplate.IndexOf(linkInfo.Url, StringComparison.InvariantCulture);
                        int promptEnd = promptStart + Encoding.Default.GetBytes(linkInfo.Url).Length;
                        var index = new FacetIndex(promptStart, promptEnd);
                        var link = FacetFeature.CreateLink(card.url);
                        facet = new Facet(index, link);
                    },
                    async error =>
                    {
                        Console.WriteLine($"Error: {error.StatusCode} {error.Detail}");
                    }
                    );
            }

            if (!Debugger.IsAttached)
            {
                if (facet != null && image != null)
                {
                    var postResult = await atProtocol.Repo.CreatePostAsync(postTemplate, new[] { facet }, new ImagesEmbed(image, $"Link to {linkInfo.Url}"));

                    postResult.Switch(
                        success =>
                        {
                            Console.WriteLine($"Post: {success.Uri} {success.Cid}");
                        },
                        error =>
                        {
                            Console.WriteLine($"Error: {error.StatusCode} {error.Detail}");
                        });
                }
                else
                {
                    var postResult = await atProtocol.Repo.CreatePostAsync(postTemplate);
                    postResult.Switch(
                        success =>
                        {
                            Console.WriteLine($"Post: {success.Uri} {success.Cid}");
                        },
                        error =>
                        {
                            Console.WriteLine($"Error: {error.StatusCode} {error.Detail}");
                        }
                        );
                }
            }
        }

    }
}
