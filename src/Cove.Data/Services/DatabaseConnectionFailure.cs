using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Cove.Data.Services;

/// <summary>
/// Whether the connection a failed command ran on was broken by the failure. Npgsql throws the same
/// exception when a command outlives its timeout on a healthy server, which cancels the query and keeps the
/// connection open, and when the server stops answering mid-command, which breaks it. Only the connection
/// tells them apart, so <see cref="DatabaseConnectionFailureInterceptor"/> records its state on the exception
/// as the command fails.
/// </summary>
internal static class DatabaseConnectionFailure
{
    private const string ConnectionBrokenKey = "Cove.Database.ConnectionBroken";

    public static void Record(Exception exception, DbConnection? connection)
    {
        if (connection is NpgsqlConnection npgsql && npgsql.FullState.HasFlag(ConnectionState.Broken))
            MarkConnectionBroken(exception);
    }

    public static void MarkConnectionBroken(Exception exception) => exception.Data[ConnectionBrokenKey] = true;

    /// <summary>Whether a command failure in <paramref name="exception"/>'s chain broke its connection.</summary>
    public static bool ConnectionBroke(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data[ConnectionBrokenKey] is true)
                return true;
        }

        return false;
    }
}

/// <summary>
/// Records on a failed command's exception whether its connection broke. It reads state the failure already
/// left behind and does no other work. Exceptions thrown while reading a result set, and commands run on a
/// connection outside the context, are not reported to interceptors and carry no record.
/// </summary>
internal sealed class DatabaseConnectionFailureInterceptor : DbCommandInterceptor
{
    public static DatabaseConnectionFailureInterceptor Instance { get; } = new();

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => DatabaseConnectionFailure.Record(eventData.Exception, eventData.Connection);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        DatabaseConnectionFailure.Record(eventData.Exception, eventData.Connection);
        return Task.CompletedTask;
    }
}
