using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Tweetinvi;
using Tweetinvi.Core.Web;
using Tweetinvi.Models;

namespace isaacldev.corefn
{
    public class TweetsV2Poster
    {
        // ----------------- Fields ----------------

        private readonly ITwitterClient client;

        // ----------------- Constructor ----------------

        public TweetsV2Poster(ITwitterClient client)
        {
            this.client = client;
        }

        public Task<ITwitterResult> PostTweet(TweetV2PostRequest tweetParams)
        {
            return this.client.Execute.AdvanceRequestAsync(
                (ITwitterRequest request) =>
                {
                    var jsonBody = this.client.Json.Serialize(tweetParams);

                    // Technically this implements IDisposable,
                    // but if we wrap this in a using statement,
                    // we get ObjectDisposedExceptions,
                    // even if we create this in the scope of PostTweet.
                    //
                    // However, it *looks* like this is fine.  It looks
                    // like Microsoft's HTTP stuff will call
                    // dispose on requests for us (responses may be another story).
                    // See also: https://stackoverflow.com/questions/69029065/does-stringcontent-get-disposed-with-httpresponsemessage
                    var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                    request.Query.Url = "https://api.twitter.com/2/tweets";
                    request.Query.HttpMethod = Tweetinvi.Models.HttpMethod.POST;
                    request.Query.HttpContent = content;
                }
            );
        }
    }

    /// <summary>
    /// There are a lot more fields according to:
    /// https://developer.twitter.com/en/docs/twitter-api/tweets/manage-tweets/api-reference/post-tweets
    /// but these are the ones we care about for our use case.
    /// </summary>
    public class TweetV2PostRequest
    {
        /// <summary>
        /// The text of the tweet to post.
        /// </summary>
        [JsonProperty("text")]
        public string Text { get; set; } = string.Empty;
    }
}