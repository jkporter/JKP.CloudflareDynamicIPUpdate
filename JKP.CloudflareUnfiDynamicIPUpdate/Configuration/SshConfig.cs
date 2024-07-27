using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JKP.CloudflareDynamicIPUpdate.Configuration
{
    public class SshConfig
    {
        public string UserName { get; set; }

        public string Password { get; set; }

        public string Host { get; set; }

        public string Interface { get; set; }
    }
}
