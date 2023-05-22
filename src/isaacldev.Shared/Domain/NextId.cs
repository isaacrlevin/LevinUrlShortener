

using Azure;
using Azure.Data.Tables;
using System;

namespace isaacldev.domain
{
    public class NextId : ITableEntity 
    {
        public int Id { get; set; }

        public string RowKey { get; set; } = default!;

        public string PartitionKey { get; set; } = default!;


        public ETag ETag { get; set; } = default!;

        public DateTimeOffset? Timestamp { get; set; } = default!;
    }
}
