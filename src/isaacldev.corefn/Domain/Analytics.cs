﻿using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace isaacldev.domain
{
    public class Analytics
    {
        public bool Validate(ShortRequest input)
        {
            if (string.IsNullOrWhiteSpace(input.Input))
            {
                throw new Exception("Need a URL to shorten!");
            }

            var urlTest = new UriBuilder(input.Input);

            bool tagMediums = input.Mediums != null && input.Mediums.Any();
            var utm = input.TagUtm.HasValue && input.TagUtm.Value;
            var wt = input.TagWt.HasValue && input.TagWt.Value;
            var tag = utm || wt;
            var tagTitle = input.Title != null && !string.IsNullOrEmpty(input.Title);

            if (tagMediums && !tag)
            {
                throw new Exception("Must choose either UTM or WT when mediums are passed.");
            }

            if (tag && !tagMediums)
            {
                throw new Exception("Can't specify a tag without at least one medium.");
            }

            return tagMediums && tagTitle;
        }

        public bool TagUtm(ShortRequest input)
        {
            return input.TagUtm.HasValue && input.TagUtm.Value;
        }

        public bool TagWt(ShortRequest input)
        {
            return input.TagWt.HasValue && input.TagWt.Value;
        }

        public async Task<List<ShortResponse>> BuildAsync(
            ShortRequest input,
            string source,
            string host,
            Func<string> getCode,
            Func<ITableEntity, Task> save,
            Action<string> log,
            Func<string, NameValueCollection> parseQueryString
            )
        {
            var result = new List<ShortResponse>();
            var title = input.Title;
            var message = input.Message ?? "";
            foreach (var medium in input.Mediums)
            {
                var uri = new UriBuilder(input.Input)
                {
                    Port = -1
                };
                var parameters = parseQueryString(uri.Query);
                if (input.TagUtm.HasValue && input.TagUtm.Value)
                {
                    parameters.Add(Utility.UTM_SOURCE, source);
                    parameters.Add(Utility.UTM_MEDIUM, medium);
                    parameters.Add(Utility.UTM_CAMPAIGN, input.Campaign);
                }
                if (input.TagWt.HasValue && input.TagWt.Value)
                {
                    parameters.Add(Utility.WTMCID, $"{input.Campaign}-{medium}-{source}");
                }
                uri.Query = parameters.ToString();
                result.Add(await Utility.SaveUrlAsync(
                    uri.ToString(),
                    medium,
                    host,
                    title,
                    message,
                    getCode,
                    log,
                    save));                
            }
            return result;
        }
    }
}
