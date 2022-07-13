using Azure;
using Azure.Data.Tables;
using System;
using System.Runtime.CompilerServices;

namespace isaacldev.domain
{
    public class ShortUrl : ITableEntity
    {
        public string Url { get; set; }
        public string Medium { get; set; }
        public bool Posted { get; set; }
        public string Message { get; set; }
        public string Title { get; set; }
        public string RowKey { get; set; } = default!;

        public string PartitionKey { get; set; } = default!;

        public ETag ETag { get; set; } = default!;

        public DateTimeOffset? Timestamp { get; set; } = default!;
    }
}
