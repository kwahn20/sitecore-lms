﻿using System;
using System.Collections.Generic;
using System.Text;

namespace ordercloud.integrations.docebo.Models
{
    public class DoceboSubscriptionResponse
    {
        public DoceboSubscriptionData data { get; set; }
        public string version { get; set; }
        public List<object> _links { get; set; }
    }

    public class DoceboSubscriptionData
    {
        public bool success { get; set; }
        public bool background_job { get; set; }
    }

}
