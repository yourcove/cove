using System.Buffers.Binary;
using System.Text;
using Cove.Core.Common;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cove.Api.Services;

internal static class ScanPostgresStage
{
    internal static NpgsqlConnection? TryOpen(CoveContext db)
    {
        if (!db.Database.IsRelational() || db.Database.GetDbConnection() is not NpgsqlConnection source)
            return null;

        var settings = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            Pooling = false,
            Enlist = false,
            Multiplexing = false,
            CommandTimeout = 300,
        };
        var connection = source.CloneWith(settings.ConnectionString);
        connection.Open();
        return connection;
    }
}

internal static class ScanSortKey
{
    internal static readonly Func<string, byte[]> Ordinal = static value => Encode(value);
    private static readonly uint[] OrdinalIgnoreCaseRanks = BuildOrdinalIgnoreCaseRanks();
    internal static readonly Func<string, byte[]> OrdinalIgnoreCase = CreateOrdinalIgnoreCaseKey;
    internal static readonly Func<string, byte[]> Filesystem =
        FilesystemPaths.PathComparison == StringComparison.OrdinalIgnoreCase ? OrdinalIgnoreCase : Ordinal;
    internal static readonly Func<string, byte[]> DirectoryDepth = static value =>
    {
        var text = OrdinalIgnoreCase(value);
        var result = new byte[text.Length + sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(result, value.Count(character => character == '/'));
        text.CopyTo(result.AsSpan(sizeof(int)));
        return result;
    };

    private static byte[] Encode(string value) => Encoding.BigEndianUnicode.GetBytes(value);

    private static byte[] CreateOrdinalIgnoreCaseKey(string value)
    {
        var result = new byte[value.Length * sizeof(uint)];
        var destination = result.AsSpan();
        var output = 0;
        for (var index = 0; index < value.Length; index++)
        {
            uint rank;
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                rank = (uint)Rune.ToUpperInvariant(new Rune(value[index], value[++index])).Value;
            }
            else
            {
                rank = OrdinalIgnoreCaseRanks[value[index]];
            }
            BinaryPrimitives.WriteUInt32BigEndian(destination[output..], rank);
            output += sizeof(uint);
        }
        return result[..output];
    }

    private static uint[] BuildOrdinalIgnoreCaseRanks()
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var characters = Enumerable.Range(char.MinValue, char.MaxValue + 1)
            .Select(value => ((char)value).ToString())
            .Order(comparer)
            .ThenBy(value => value[0])
            .ToArray();
        var ranks = new uint[characters.Length];
        uint rank = 0;
        for (var index = 0; index < characters.Length; index++)
        {
            if (index > 0 && !comparer.Equals(characters[index - 1], characters[index])) rank++;
            ranks[characters[index][0]] = rank;
        }
        return ranks;
    }

    internal static byte[] PrefixUpperBound(byte[] prefix)
    {
        ArgumentOutOfRangeException.ThrowIfZero(prefix.Length);
        var upper = prefix.ToArray();
        for (var index = upper.Length - 1; index >= 0; index--)
        {
            if (upper[index] == byte.MaxValue) continue;
            upper[index]++;
            return upper[..(index + 1)];
        }
        return [.. prefix, 0];
    }
}

internal sealed class ScanDirectoryDepthComparer : StringComparer
{
    internal static readonly ScanDirectoryDepthComparer Instance = new();

    public override int Compare(string? x, string? y)
    {
        var depth = (x?.Count(character => character == '/') ?? 0)
            .CompareTo(y?.Count(character => character == '/') ?? 0);
        return depth != 0 ? depth : StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }

    public override bool Equals(string? x, string? y) => StringComparer.OrdinalIgnoreCase.Equals(x, y);
    public override int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
}
