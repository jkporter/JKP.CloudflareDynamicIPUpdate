using System.Collections.Frozen;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudFlare.Client;
using CloudFlare.Client.Api.Result;
using CloudFlare.Client.Api.Zones;
using CloudFlare.Client.Api.Zones.DnsRecord;
using CloudFlare.Client.Enumerators;
using JKP.CloudflareDynamicIPUpdate.Configuration;
using JKP.CloudflareDynamicIPUpdate.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace JKP.CloudflareDynamicIPUpdate;

public partial class Worker : BackgroundService
{
    private static readonly IPAddressEqualityComparer IPAddressEqualityComparer = new();

    public static readonly TimeSpan LifeTimeForever = TimeSpan.FromSeconds(4294967295U);

    private IReadOnlyList<Zone> _zones = [];

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly DynamicUpdateConfig _dynamicUpdateConfig;
    private readonly ILogger<Worker> _logger;

    private readonly TimeSpan _period;

    public Worker(IOptions<DynamicUpdateConfig> dynamicUpdateConfig, ILogger<Worker> logger)
    {
        _dynamicUpdateConfig = dynamicUpdateConfig.Value;
        _period = TimeSpan.FromSeconds(dynamicUpdateConfig.Value.CheckInterval);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var currentAddresses = new HashSet<IPAddress>();

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Getting addresses on interface {interfaceName}", _dynamicUpdateConfig.Ssh.Interface);
            try
            {
                var address = await GetAddress(stoppingToken);
                if (address is not null)
                {
                    var logIPAddressesMessage = new StringBuilder("Found {ipAddressCount} address(es):");
                    var logIPAddressesArgs = new List<object>();
                    var ipAddresses = new HashSet<IPAddress>(GetIPAddresses(address.AddressInformation)
                        .Where(ipAddress =>
                        {
                            if (ipAddress.AddressFamily is not AddressFamily.InterNetwork |
                                !_dynamicUpdateConfig.UpdateIPv4 &&
                                ipAddress.AddressFamily is not AddressFamily.InterNetworkV6 |
                                !_dynamicUpdateConfig.UpdateIPv6) return false;

                            logIPAddressesMessage.Append($"{Environment.NewLine}  {{ipAddressFamily}} address: {{ipAddress}}");

                            logIPAddressesArgs.Add(AsString(ipAddress.AddressFamily));
                            logIPAddressesArgs.Add(ipAddress);

                            return true;
                        }), IPAddressEqualityComparer);

                    if (ipAddresses.Count == 0)
                    {
                        _logger.LogInformation("No IP addresses found.");
                        currentAddresses.Clear();
                    }
                    else
                    {
                        logIPAddressesArgs.Insert(0, ipAddresses.Count);
                        _logger.LogInformation(logIPAddressesMessage.ToString(), logIPAddressesArgs.ToArray());

                        currentAddresses.IntersectWith(ipAddresses);
                        ipAddresses.ExceptWith(currentAddresses);

                        if (ipAddresses.Count > 0)
                        {
                            await UpdateDnsRecords(_dynamicUpdateConfig.Domain, ipAddresses, cancellationToken: stoppingToken);
                            currentAddresses.UnionWith(ipAddresses);
                        }
                        else
                        {
                            _logger.LogInformation("No DNS records to update.");
                        }
                    }
                }
                else
                {
                    _logger.LogError("Could not find interface {interfaceName}", _dynamicUpdateConfig.Ssh.Interface);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "An error occurred.");
            }

            _logger.LogInformation("Checking for IP changes in {time}", _period);

            await Task.Delay(_period, stoppingToken);
        }
    }

    private async Task<AddressObject?> GetAddress(CancellationToken cancellationToken = default)
    {
        using var connectionInfo = new KeyboardInteractiveConnectionInfo(_dynamicUpdateConfig.Ssh.Host, _dynamicUpdateConfig.Ssh.UserName);
        connectionInfo.AuthenticationPrompt += (_, args) =>
        {
            foreach (var authenticationPrompt in args.Prompts)
                authenticationPrompt.Response = _dynamicUpdateConfig.Ssh.Password;
        };
        using var client = new SshClient(connectionInfo);
        _logger.LogInformation("SSHing into {host} as {user}", connectionInfo.Host, connectionInfo.Username);
        await client.ConnectAsync(cancellationToken);

        using var cmd = client.CreateCommand($"ip -j a show '{_dynamicUpdateConfig.Ssh.Interface.Replace("'", @"'\''")}'");
        _logger.LogInformation("Executing command: {command}", cmd.CommandText);

        await cmd.ExecuteAsync(cancellationToken);

        if (cmd.ExitStatus.GetValueOrDefault() != 0)
            throw new Exception(cmd.Error);

        var scopeConvertor = new ScopeConvertor();
        try
        {
            scopeConvertor.Scopes = await GetScopeTable(cancellationToken) ?? new Dictionary<int, string>();
        }
        catch
        {
        }

        var options = new JsonSerializerOptions(JsonSerializerOptions);
        options.Converters.Add(scopeConvertor);
        return JsonSerializer.Deserialize<AddressObject[]>(cmd.Result, options)![0];
    }

    [GeneratedRegex(@"^[ \t]*(?:(?:#|$)|(?<idName>((?<hex>0x)(?<id>[0-9A-Fa-f])|(?<id>\d+)\s+(?<name>\S+)(?:$|\s+#))))")]
    private static partial Regex IdNameRegex();

    static readonly string[] RtScopesPaths = new[] { "/etc/iproute2/rt_scopes", "/usr/lib/iproute2/rt_scopes" };
    
    private async Task<IReadOnlyDictionary<int, string>?> GetScopeTable(CancellationToken cancellationToken = default)
    {
        using var client = new SftpClient(_dynamicUpdateConfig.Ssh.Host, 22, _dynamicUpdateConfig.Ssh.UserName, _dynamicUpdateConfig.Ssh.Password);

        _logger.LogInformation("Connecting to {host} as {userName} via SFTP", _dynamicUpdateConfig.Ssh.Host, _dynamicUpdateConfig.Ssh.UserName);
        await client.ConnectAsync(cancellationToken);

        var table = new Dictionary<int, string>();

        SftpFileStream? input = null;
        
        string? path = null;
        foreach (var rtScopesPath in RtScopesPaths)
        {
            try
            {
                _logger.LogInformation("Opening {path}", rtScopesPath);
                input = await client.OpenAsync(rtScopesPath, FileMode.Open, FileAccess.Read, cancellationToken);
                path = rtScopesPath;
                break;
            }
            catch (SshConnectionException)
            {
                _logger.LogError("Problem with opening {path}", rtScopesPath);
                if (input is not null)
                    await input.DisposeAsync();
                input = null;
            }
        }

        if (input == null)
            return null;

        using var reader = new StreamReader(input);
        var line = await reader.ReadLineAsync(cancellationToken);
        while (line is not null)
        {
            var m = IdNameRegex().Match(line);
            if (!m.Success)
                throw new Exception($"Database {path} is corrupted at {line}");

            if (m.Groups["idName"].Success)
            {
                var id = int.Parse(m.Groups["id"].ValueSpan,
                    m.Groups["hex"].Success ? NumberStyles.AllowHexSpecifier : NumberStyles.None);

                const int size = 256;
                if (id is >= 0 and <= size -1 /* Deviates from source code (<= size) as a value of 256 will overflow
                                               * the defined array in source. */)
                {
                    table[id] = m.Groups["name"].Value;
                    _logger.LogInformation("Added scope name {name} for {id}", table[id], id);
                }
                else
                {
                    _logger.LogWarning("Scope {id} is outside range. Must be between 0 and 255", id);
                }
            }
            line = await reader.ReadLineAsync(cancellationToken);
        }

        return table.ToFrozenDictionary();
    }

    private async Task<Zone?> GetZone(string hostName, CancellationToken cancellationToken = default)
    {
        var zone = _zones.SingleOrDefault(zone => hostName.EndsWith(zone.Name, StringComparison.InvariantCultureIgnoreCase));
        if (zone is not null)
            return zone;

        using var client = new CloudFlareClient(_dynamicUpdateConfig.Cloudflare.ApiToken);
        _logger.LogInformation("Searching for zone encompassing {hostName}", hostName);

        _zones = (await client.Zones.GetAsync(cancellationToken: cancellationToken)).Result;
        zone = _zones.Where(zone => hostName.EndsWith(zone.Name, StringComparison.InvariantCultureIgnoreCase))
            .OrderByDescending(zone => zone.Name.Length).FirstOrDefault();

        return zone;
    }

    private IEnumerable<IPAddress> GetIPAddresses(IReadOnlyList<AddressInformation> addressInformation)
    {
        return addressInformation
            .Where(addressInfo => addressInfo.Scope is Scope.Universe)
            .Select(addressInfo => addressInfo.Local)
            .Where(ipAddress =>
                ipAddress.AddressFamily is AddressFamily.InterNetwork & _dynamicUpdateConfig.UpdateIPv4 ||
                ipAddress.AddressFamily is AddressFamily.InterNetworkV6 & _dynamicUpdateConfig.UpdateIPv6);
    }

    private async Task UpdateDnsRecords(string hostName, IEnumerable<IPAddress> ipAddresses,
        CancellationToken cancellationToken = default)
    {
        var zone = await GetZone(hostName, cancellationToken);
        if (zone == null)
        {
            _logger.LogError("Could not find zone for {hostName}.", hostName);
            return;
        }

        _logger.LogInformation("Found zone {zone} encompassing {hostName}", zone.Name, hostName);

        using var client = new CloudFlareClient(_dynamicUpdateConfig.Cloudflare.ApiToken);
        var dnsRecordsByType = (await client.Zones.DnsRecords.GetAsync(zone.Id, new DnsRecordFilter { Name = hostName },
                cancellationToken: cancellationToken)).Result
            .Where(dnsRecord => dnsRecord.Type is DnsRecordType.A & _dynamicUpdateConfig.UpdateIPv4 ||
                                dnsRecord.Type is DnsRecordType.Aaaa & _dynamicUpdateConfig.UpdateIPv6)
            .Select((dnsRecord, index) => (DnsRecord: dnsRecord, Order: index + 1))
            .GroupBy(tuple => tuple.DnsRecord.Type)
            .ToDictionary(dnsRecord => dnsRecord.Key, dnsRecord => dnsRecord.ToList());

        foreach (var ipAddress in ipAddresses)
        {
            DnsRecordType? matchingDnsRecordType = ipAddress.AddressFamily switch
            {
                AddressFamily.InterNetwork => DnsRecordType.A,
                AddressFamily.InterNetworkV6 => DnsRecordType.Aaaa,
                _ => null
            };

            if (matchingDnsRecordType is null)
            {
                continue;
            }

            var matchingDnsRecordsForAddressFamily =
                dnsRecordsByType.TryGetValue(matchingDnsRecordType.Value, out var value)
                    ? value
                    : [];
            if (matchingDnsRecordsForAddressFamily.Count == 0)
            {
                _logger.LogInformation("Creating {dnsRecordType} record for {hostname} with {ipAddress}",
                    AsString(matchingDnsRecordType.Value), hostName, ipAddress);
                _ = ThrowIfError(await client.Zones.DnsRecords.AddAsync(zone.Id,
                    new NewDnsRecord
                    { Name = hostName, Content = ipAddress.ToString(), Type = matchingDnsRecordType.Value },
                    cancellationToken));
            }
            else
            {
                var matchingDnsRecords = matchingDnsRecordsForAddressFamily.Where(tuple =>
                        IPAddress.TryParse(tuple.DnsRecord.Content, out var content) && Equals(content, ipAddress))
                    .ToArray();

                if (matchingDnsRecords.Length > 0)
                {
                    _logger.LogInformation("The {dnsRecordType} record {id} for {ipAddress} does not require updating.",
                        AsString(matchingDnsRecords[0].DnsRecord.Type), matchingDnsRecords[0].DnsRecord.Id, ipAddress);
                    matchingDnsRecordsForAddressFamily.Remove(matchingDnsRecords[0]);
                }
                else
                {
                    _logger.LogInformation(
                        "Updating {dnsRecordType} record {id} for {hostname} with from {previousIPAddress} to {ipAddress}",
                        AsString(matchingDnsRecordsForAddressFamily[0].DnsRecord.Type),
                        matchingDnsRecordsForAddressFamily[0].DnsRecord.Id,
                        hostName,
                        matchingDnsRecordsForAddressFamily[0].DnsRecord.Content, ipAddress);
                    _ = ThrowIfError(await client.Zones.DnsRecords.UpdateAsync(zone.Id,
                        matchingDnsRecordsForAddressFamily[0].DnsRecord.Id,
                        new ModifiedDnsRecord
                        {
                            Content = ipAddress.ToString(),
                            Name = matchingDnsRecordsForAddressFamily[0].DnsRecord.Name,
                            Type = matchingDnsRecordsForAddressFamily[0].DnsRecord.Type
                        },
                        cancellationToken));

                    matchingDnsRecordsForAddressFamily.RemoveAt(0);
                }
            }
        }

        foreach (var (dnsRecord, _) in dnsRecordsByType.Values.SelectMany(tuple => tuple).OrderBy(tuple => tuple.Order))
        {
            _logger.LogInformation("Removing {dnsRecordType} record {id} with {ipAddress} for {hostname}",
                AsString(dnsRecord.Type),
                dnsRecord.Id, ipAddresses, hostName);
            _ = ThrowIfError(await client.Zones.DnsRecords.DeleteAsync(zone.Id, dnsRecord.Id, cancellationToken));
        }
    }

    private static T ThrowIfError<T>(CloudFlareResult<T> result)
    {
        if (!result.Success)
            throw new Exception(result.Errors[0].Message);
        return result.Result;
    }

    private static string AsString(AddressFamily addressFamily) => addressFamily switch
    {
        AddressFamily.InterNetwork => "IPv4",
        AddressFamily.InterNetworkV6 => "IPv6",
        _ => throw new NotSupportedException()
    };

    private static string AsString<T>(T value) where T : Enum
    {
        var enumType = typeof(T);
        var name = Enum.GetName(enumType, value);
        if (name is null)
            return value.ToString();

        var enumMemberAttribute = (enumType.GetField(name)!.GetCustomAttributes(typeof(EnumMemberAttribute), true))
            .Cast<EnumMemberAttribute>().SingleOrDefault();
        return enumMemberAttribute?.Value ?? value.ToString();
    }
}