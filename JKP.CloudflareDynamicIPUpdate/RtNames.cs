using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using JKP.CloudflareDynamicIPUpdate.Serialization;
using Renci.SshNet;

namespace JKP.CloudflareDynamicIPUpdate;

internal static partial class RtNames
{ 
   public static IReadOnlyDictionary<Scope, string> RtScopeTable = new Dictionary<Scope, string>
    {
        { Scope.Universe, "global" },
        { Scope.Nowhere, "nowhere" },
        { Scope.Host, "host" },
        { Scope.Link, "link" },
        { Scope.Site, "site" }
    }.ToFrozenDictionary();

    public static async Task TableInitializeAsync(ISftpClient sftpClient, string path, IDictionary<Scope, string> table,
        int size, ILogger logger, CancellationToken cancellationToken)
    {
        await using var input = await sftpClient.OpenAsync(path, FileMode.Open, FileAccess.Read, cancellationToken);
        using var reader = new StreamReader(input);
        (int Result, (int? Id, string Name)? IdName) ret;
        while ((ret = await ReadIdNameAsync(reader)).Result != 0)
        {
            if (ret.Result == -1)
            {
                var ex = new InvalidDataException();
                logger.LogError(ex, "Database {path} is corrupted at {line}", path, ret.IdName!.Value.Name);
                throw ex;
            }

            var (id, name) = ret.IdName!.Value;
            if (id < 0 || id >= size)
                continue;

            table[(Scope)id!.Value] = name;
        }
    }

    [GeneratedRegex(@"^[ \t]*((#|$)|(0x\s*(\+|(?<idBase16MinusSign>-))?(0[xX])?(?<idBase16Digits>[0-9A-Fa-f]+)|\s*(?<id>[+-]?\d+))\s+\s*(?<name>\S+)($|\s+#))", RegexOptions.ExplicitCapture | RegexOptions.Singleline)]
    private static partial Regex IdNameRegex();

    private static async Task<(int Result, (int? Id, string Name)? IdName)> ReadIdNameAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            var match = IdNameRegex().Match(line);
            if (!match.Success)
                return (-1, (null, line));

            var nameGroup = match.Groups["name"];
            if (!nameGroup.Success)
                continue;

            int id;

            var idHexGroup = match.Groups["idBase16Digits"];
            if (idHexGroup.Success)
            {
                id = int.Parse(idHexGroup.ValueSpan, NumberStyles.AllowHexSpecifier);
                if (match.Groups["idBase16MinusSign"].Success)
                    id = -id;
            }
            else
            {
                id = int.Parse(match.Groups["id"].ValueSpan, NumberStyles.AllowLeadingSign);
            }

            return (1, (id, nameGroup.Value));
        }

        return (0, null);
    }
}