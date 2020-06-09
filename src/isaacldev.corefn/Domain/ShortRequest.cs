using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace isaacldev.domain
{
    public class ShortRequest
    {
        public bool? TagUtm { get; set; }

        public string Title { get; set; }
        public string Message { get; set; }

        public bool? TagWt { get; set; }

        public string Campaign { get; set; }

        public string ShortCode { get; set; }

        public string[] Mediums { get; set; }

        public string Input { get; set; }
    }
}
