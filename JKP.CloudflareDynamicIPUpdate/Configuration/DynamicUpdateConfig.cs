namespace JKP.CloudflareDynamicIPUpdate.Configuration
{
    public class DynamicUpdateConfig
    {
        public string Domain { get; set; }

        public bool UpdateIPv4 { get; set; } = true;

        public bool UpdateIPv6 { get; set;} = true;

        public ICollection<object> Scopes { get; set; } = ["global"];

        public int CheckInterval { get; set; } = 300;

        public SshConfig Ssh { get; set; }

        public CloudflareConfig Cloudflare { get; set; }
    }
}
