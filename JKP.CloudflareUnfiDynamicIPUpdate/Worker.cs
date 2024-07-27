using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Text.Json;
using CloudFlare.Client;
using CloudFlare.Client.Api.Result;
using CloudFlare.Client.Api.Zones.DnsRecord;
using CloudFlare.Client.Enumerators;
using JKP.CloudflareDynamicIPUpdate.Configuration;
using JKP.CloudflareDynamicIPUpdate.Serialization;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace JKP.CloudflareDynamicIPUpdate;

public class Worker : BackgroundService
{
    private static readonly IPAddressEqualityComparer IPAddressEqualityComparer = new();

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
        _period = TimeSpan.FromSeconds(_dynamicUpdateConfig.CheckInterval);
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
                    var ipAddresses = new HashSet<IPAddress>(IPAddressEqualityComparer);
                    foreach (var ipAddress in GetIPAddresses(address.AddressInformation))
                    {
                        _logger.LogInformation("Found {ipAddressFamily} address: {ipAddress}",
                            AsString(ipAddress.AddressFamily), ipAddress);
                        if (!currentAddresses.Contains(ipAddress))
                            ipAddresses.Add(ipAddress);
                    }

                    if (ipAddresses.Count > 0)
                    {
                        await UpdateDnsRecords(_dynamicUpdateConfig.Domain, ipAddresses, cancellationToken: stoppingToken);
                        currentAddresses = new HashSet<IPAddress>(ipAddresses, IPAddressEqualityComparer);
                    }
                    else
                    {
                        _logger.LogInformation("No DNS records to update.");
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
        connectionInfo.AuthenticationPrompt += (sender, args) =>
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

        try
        {
            return JsonSerializer.Deserialize<AddressObject[]>(cmd.Result, JsonSerializerOptions)![0];
        }
        catch (Exception)
        {
            throw new JsonException();
        }
    }

    private IEnumerable<IPAddress> GetIPAddresses(IReadOnlyList<AddressInformation> addressInformation)
    {
        return addressInformation
            .Where(addressInfo => addressInfo.Scope == "global")
            .Select(addressInfo => addressInfo.Local)
            .Where(ipAddress => ipAddress.AddressFamily is AddressFamily.InterNetwork && _dynamicUpdateConfig.UpdateIPv4 || ipAddress.AddressFamily is AddressFamily.InterNetworkV6 && _dynamicUpdateConfig.UpdateIPv6);
    }

    private async Task UpdateDnsRecords(string hostName, IEnumerable<IPAddress> ipAddresses, CancellationToken cancellationToken = default)
    {
        var ipAddressesByDnsRecordType = ipAddresses.ToDictionary(ipAddress => ipAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => DnsRecordType.A,
            AddressFamily.InterNetworkV6 => DnsRecordType.Aaaa,
            _ => throw new NotSupportedException()
        }, ipAddress => ipAddress);

        using var client = new CloudFlareClient(_dynamicUpdateConfig.Cloudflare.ApiToken);
        _logger.LogInformation("Searching for zone encompassing {hostName}", hostName);

        var zones = await client.Zones.GetAsync(cancellationToken: cancellationToken);

        var zone = zones.Result.SingleOrDefault(zone =>
            hostName.EndsWith(zone.Name, StringComparison.InvariantCultureIgnoreCase));

        if (zone == null)
        {
            _logger.LogError("Could not find zone for {hostName}.", hostName);
            return;
        }

        _logger.LogInformation("Found zone {zone} encompassing {hostName}", zone.Name, hostName);

        var dnsRecords = (await client.Zones.DnsRecords.GetAsync(zone.Id, new DnsRecordFilter { Name = hostName },
            cancellationToken: cancellationToken)).Result.Where(dnsRecord =>
            dnsRecord.Type is DnsRecordType.A && _dynamicUpdateConfig.UpdateIPv4 ||
            dnsRecord.Type is DnsRecordType.Aaaa && _dynamicUpdateConfig.UpdateIPv6);

        foreach (var dnsRecord in dnsRecords)
        {
            switch (ipAddressesByDnsRecordType.TryGetValue(dnsRecord.Type, out var ipAddress))
            {
                case false:
                    {
                        _logger.LogInformation("Removing {dnsRecordType} record for {hostname}", AsString(dnsRecord.Type),
                            hostName);
                        var ex = GetException(
                            await client.Zones.DnsRecords.DeleteAsync(zone.Id, dnsRecord.Id, cancellationToken));
                        if (ex is not null)
                            throw ex;
                    }
                    break;
                case true when !IPAddress.TryParse(dnsRecord.Content, out var content) || !ipAddress!.Equals(content):
                    {
                        _logger.LogInformation(
                            "Updating {dnsRecordType} record for {hostname} with from {currentIPAddress} to {ipAddress}",
                            AsString(dnsRecord.Type), hostName,
                            content, ipAddress);
                        var ex = GetException(await client.Zones.DnsRecords.UpdateAsync(zone.Id, dnsRecord.Id,
                            new ModifiedDnsRecord
                            { Content = ipAddress!.ToString(), Name = dnsRecord.Name, Type = dnsRecord.Type },
                            cancellationToken));
                        if (ex is not null)
                            throw ex;
                    }
                    goto default;
                case true:
                    _logger.LogInformation("The {dnsRecordType} record does not require updating.",
                        AsString(dnsRecord.Type));
                    goto default;
                default:
                    ipAddressesByDnsRecordType.Remove(dnsRecord.Type);
                    break;
            }
        }

        foreach (var (dnsRecordType, ipAddress) in ipAddressesByDnsRecordType)
        {
            _logger.LogInformation("Creating {dnsRecordType} record for {hostname} with {ipAddress}", AsString(dnsRecordType), hostName, ipAddress);
            var ex = GetException(await client.Zones.DnsRecords.AddAsync(zone.Id, new NewDnsRecord { Name = hostName, Content = ipAddress.ToString(), Type = dnsRecordType },
                cancellationToken));
            if (ex is not null)
                throw ex;
        }
    }

    private static Exception? GetException<T>(CloudFlareResult<T> result)
    {
        return result.Success ? null : new Exception(result.Errors[0].Message);
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