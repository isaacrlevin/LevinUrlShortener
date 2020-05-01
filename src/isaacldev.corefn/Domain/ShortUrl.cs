using Microsoft.WindowsAzure.Storage.Table;
using System.Runtime.CompilerServices;

namespace isaacldev.domain
{
    public class ShortUrl : TableEntity
    {
        public string Url { get; set; }
        public string Medium { get; set; }
        public bool Posted { get; set; }
        public string Message { get; set; }
        public string Title { get; set; }
    }
}
