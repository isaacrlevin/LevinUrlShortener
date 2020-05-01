using Microsoft.WindowsAzure.Storage.Table;

namespace isaacldev.domain
{
    public class NextId : TableEntity 
    {
        public int Id { get; set; }
    }
}
