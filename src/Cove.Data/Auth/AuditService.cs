using System.Text.Json;
using System.Threading.Channels;
using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cove.Data.Auth;

/// <summary>
/// Background-flushed audit log writer. <see cref="LogAsync"/> never throws and never blocks.
/// </summary>
public sealed class AuditService : BackgroundService, IAuditService
{
    private readonly Channel<QueueItem> _channel = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(8192)
    {
        SingleReader = true,
        // Preserve FIFO barriers used by lifecycle callers. Ordinary audit writes remain non-blocking:
        // TryWrite returns false when this bounded queue is full, while FlushAsync can wait for room.
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly IServiceProvider _services;
    private readonly ILogger<AuditService> _log;

    public AuditService(IServiceProvider services, ILogger<AuditService> log)
    {
        _services = services;
        _log = log;
    }

    public Task LogAsync(string action, string outcome, CovePrincipal? actor = null,
        string? targetKind = null, string? targetId = null, object? detail = null,
        CancellationToken ct = default)
    {
        try
        {
            var ev = new AuditEvent
            {
                OccurredAt = DateTime.UtcNow,
                Action = action,
                Outcome = outcome,
                ActorUserId = actor?.UserId,
                ActorKind = actor?.Kind switch
                {
                    PrincipalKind.User => "user",
                    PrincipalKind.ApiToken => "api_token",
                    PrincipalKind.ShareLink => "share_link",
                    PrincipalKind.System => "system",
                    PrincipalKind.Anonymous => "anonymous",
                    _ => "system",
                },
                Ip = actor?.Ip,
                UserAgent = actor?.UserAgent,
                TargetKind = targetKind,
                TargetId = targetId,
                Detail = detail is null ? null : JsonSerializer.Serialize(detail),
            };
            _channel.Writer.TryWrite(new AuditItem(ev));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Audit log enqueue failed (suppressed)");
        }
        return Task.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(new FlushItem(completion), cancellationToken);
        await completion.Task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AuditEvent>(64);
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                if (item is FlushItem flush)
                {
                    flush.Completion.TrySetResult();
                    continue;
                }

                batch.Add(((AuditItem)item).Event);
                // drain quickly
                while (batch.Count < 64 && _channel.Reader.TryPeek(out var next) && next is AuditItem)
                {
                    _channel.Reader.TryRead(out var more);
                    batch.Add(((AuditItem)more!).Event);
                }
                try
                {
                    using var scope = _services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                    db.AuditEvents.AddRange(batch);
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Audit batch flush failed (suppressed)");
                }
                batch.Clear();
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private abstract record QueueItem;
    private sealed record AuditItem(AuditEvent Event) : QueueItem;
    private sealed record FlushItem(TaskCompletionSource Completion) : QueueItem;
}
