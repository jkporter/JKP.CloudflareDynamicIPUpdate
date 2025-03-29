using System.Collections.Frozen;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using CloudFlare.Client;
using CloudFlare.Client.Api.Result;
using CloudFlare.Client.Api.Zones;
using CloudFlare.Client.Api.Zones.DnsRecord;
using CloudFlare.Client.Enumerators;
using JKP.CloudflareDynamicIPUpdate.Configuration;
using JKP.CloudflareDynamicIPUpdate.Notification;
using JKP.CloudflareDynamicIPUpdate.Serialization;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using IPAddress = System.Net.IPAddress;

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
    private readonly IEnumerable<INotifier> _notifiers;
    private readonly ILogger<Worker> _logger;

    private readonly TimeSpan _period;

    public Worker(IOptions<DynamicUpdateConfig> dynamicUpdateConfig, IEnumerable<INotifier> notifiers, ILogger<Worker> logger)
    {
        _dynamicUpdateConfig = dynamicUpdateConfig.Value;
        _period = TimeSpan.FromSeconds(dynamicUpdateConfig.Value.CheckInterval);
        _notifiers = notifiers;
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

                        if (ipAddresses.SetEquals(currentAddresses))
                        {
                            _logger.LogInformation("No DNS records to update.");
                        }
                        else
                        {
                            await UpdateDnsRecords(_dynamicUpdateConfig.Domain, ipAddresses, cancellationToken: stoppingToken);
                            currentAddresses = ipAddresses;
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

        var scopeConvertor = new ScopeConvertor
        {
            Scopes = await GetScopeTable(connectionInfo, cancellationToken)
        };
        var options = new JsonSerializerOptions(JsonSerializerOptions);
        options.Converters.Add(scopeConvertor);
        return JsonSerializer.Deserialize<AddressObject[]>(cmd.Result, options)![0];
    }

    private static readonly string[] RtScopesPaths = ["/etc/iproute2/rt_scopes", "/usr/lib/iproute2/rt_scopes"];

    private async Task<IReadOnlyDictionary<Scope, string>> GetScopeTable(ConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
    {
        var table = new Dictionary<Scope, string>(RtNames.RtScopeTable);
        using var client = new SftpClient(connectionInfo);

        _logger.LogInformation("Connecting to {host} as {userName} via SFTP", _dynamicUpdateConfig.Ssh.Host, _dynamicUpdateConfig.Ssh.UserName);
        await client.ConnectAsync(cancellationToken);
        foreach (var rtScopesPath in RtScopesPaths)
        {
            _logger.LogInformation("Opening {path}", rtScopesPath);
            try
            {
                await RtNames.TableInitializeAsync(client, rtScopesPath, table, 256, _logger, cancellationToken);
                break;
            }
            catch
            {
            }
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
        var dnsRecords = (await client.Zones.DnsRecords.GetAsync(zone.Id, new DnsRecordFilter { Name = hostName }, cancellationToken: cancellationToken)).Result.ToList();

        List<IPAddress> ipAddressesForAddOrUpdate;
        foreach (var (dnsRecord, matchingIPAddress) in GetDnsRecordsWithMatchingIPAddress(dnsRecords, ipAddresses, out ipAddressesForAddOrUpdate))
        {
            if (matchingIPAddress is not null)
            {
                _logger.LogInformation("The {dnsRecordType} record {id} for {ipAddress} does not require updating.",
                    AsString(dnsRecord.Type), dnsRecord.Id, matchingIPAddress);
                continue;
            }

            var ipAddressesForAddOrUpdateIndex = ipAddressesForAddOrUpdate.FindIndex(ipAddress => GetDnsRecordType(ipAddress) == dnsRecord.Type);
            if (ipAddressesForAddOrUpdateIndex != -1)
            {
                var ipAddressForUpdate = ipAddressesForAddOrUpdate[ipAddressesForAddOrUpdateIndex];
                _logger.LogInformation(
                    "Updating {dnsRecordType} record {id} for {hostname} with from {previousIPAddress} to {ipAddress}",
                    AsString(dnsRecord.Type),
                    dnsRecord.Id,
                    hostName,
                    dnsRecord.Content, ipAddressForUpdate);

                _ = ThrowIfError(await client.Zones.DnsRecords.UpdateAsync(zone.Id,
                    dnsRecord.Id,
                    new ModifiedDnsRecord
                    {
                        Content = ipAddressForUpdate.ToString(),
                        Name = dnsRecord.Name,
                        Type = dnsRecord.Type,
                        Comment =
                            $"Updated on {DateTimeOffset.Now:O} from {dnsRecord.Content}{(dnsRecord.ModifiedDate is null ? string.Empty : $" on {dnsRecord.ModifiedDate.Value:O}")}"
                    },
                    cancellationToken));
                ipAddressesForAddOrUpdate.RemoveAt(ipAddressesForAddOrUpdateIndex);
                continue;
            }

            _logger.LogInformation("Removing {dnsRecordType} record {id} with {ipAddress} for {hostname}",
                AsString(dnsRecord.Type),
                dnsRecord.Id, ipAddresses, hostName);
            _ = ThrowIfError(await client.Zones.DnsRecords.DeleteAsync(zone.Id, dnsRecord.Id, cancellationToken));
        }

        foreach (var ipAddressForAdd in ipAddressesForAddOrUpdate)
        {
            var dnsRecordType = GetDnsRecordType(ipAddressForAdd)!.Value;
            _logger.LogInformation("Creating {dnsRecordType} record for {hostname} with {ipAddress}",
                AsString(dnsRecordType), hostName, ipAddressForAdd);
            _ = ThrowIfError(await client.Zones.DnsRecords.AddAsync(zone.Id,
                new NewDnsRecord
                {
                    Name = hostName, Content = ipAddressForAdd.ToString(), Type = dnsRecordType,
                    Comment = $"Created on {DateTimeOffset.Now:O}"
                },
                cancellationToken));
        }

        await Task.WhenAll(_notifiers.Select(notifier => notifier.SendNotification(cancellationToken)));
    }

    static IEnumerable<(DnsRecord DnsRecord, IPAddress? IPAddress)> GetDnsRecordsWithMatchingIPAddress(
        IEnumerable<DnsRecord> dnsRecords, IEnumerable<IPAddress> ipAddresses, out List<IPAddress> toAdd)
    {
        var dnsRecordsAndMatchingIPAddress = new List<(DnsRecord DnsRecord, IPAddress? IPAddress)>();
        var ipAddressList = ipAddresses.ToList();

        foreach (var dnsRecord in dnsRecords)
        {
            var ipAddressIndex = ipAddressList.FindIndex(ipAddress =>
                GetDnsRecordType(ipAddress) == dnsRecord.Type && ipAddress.Equals(IPAddress.Parse(dnsRecord.Content)));
            if (ipAddressIndex == -1)
            {
                dnsRecordsAndMatchingIPAddress.Add((dnsRecord, null));
                continue;
            }

            dnsRecordsAndMatchingIPAddress.Add((dnsRecord, ipAddressList[ipAddressIndex]));
            ipAddressList.RemoveAt(ipAddressIndex);
        }

        toAdd = ipAddressList;
        return dnsRecordsAndMatchingIPAddress;
    }

    static DnsRecordType? GetDnsRecordType(IPAddress ipAddress)
    {
        return ipAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => DnsRecordType.A,
            AddressFamily.InterNetworkV6 => DnsRecordType.Aaaa,
            _ => null
        };
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