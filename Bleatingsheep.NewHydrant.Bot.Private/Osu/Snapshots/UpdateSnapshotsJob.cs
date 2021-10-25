﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bleatingsheep.NewHydrant.Attributions;
using Bleatingsheep.NewHydrant.Core;
using Bleatingsheep.NewHydrant.Data;
using Bleatingsheep.OsuQqBot.Database.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sisters.WudiLib;

namespace Bleatingsheep.NewHydrant.Osu.Snapshots
{
#nullable enable
    [Component("UpdateSnapshotsJob")]
    public class UpdateSnapshotsJob : Service, IRegularAsync
    {
        private static UpdateSnapshotsJob? s_main;
        private static DateTimeOffset s_lastSeen;
        private static int s_running = 0;
        private static readonly TimeSpan s_minDelayBeforeChangingId = TimeSpan.FromMinutes(30);
        private readonly IDbContextFactory<NewbieContext> _dbContextFactory;
        private readonly DataMaintainer _dataMaintainer;
        private readonly ILogger<UpdateSnapshotsJob> _logger;

        public TimeSpan? OnUtc { get; }
        public TimeSpan? Every => TimeSpan.FromMinutes(1);

        public UpdateSnapshotsJob(IDbContextFactory<NewbieContext> dbContextFactory, DataMaintainer dataMaintainer, ILogger<UpdateSnapshotsJob> logger)
        {
            _dbContextFactory = dbContextFactory;
            _dataMaintainer = dataMaintainer;
            _logger = logger;
        }

        public async Task RunAsync(HttpApiClient api)
        {
            if (s_main != this)
            {
                if (DateTimeOffset.UtcNow < s_lastSeen + s_minDelayBeforeChangingId)
                {
                    // other connected user, skip.
                    return;
                }
                else
                {
                    s_main = this;
                }
            }
            s_lastSeen = DateTimeOffset.UtcNow;
            if (Interlocked.Exchange(ref s_running, 1) != 0)
                return;

            try
            {
                await using (var db1 = _dbContextFactory.CreateDbContext())
                {
                    var snapshotted =
                        await db1.UserSnapshots
                        .Select(s => new { s.UserId, s.Mode })
                        .Distinct()
                        .ToListAsync()
                        .ConfigureAwait(false);
                    var scheduled =
                        await db1.UpdateSchedules
                        .Select(s => new { s.UserId, s.Mode })
                        .ToListAsync()
                        .ConfigureAwait(false);
                    var toSchedule = snapshotted.Except(scheduled).Select(i => new UpdateSchedule
                    {
                        UserId = i.UserId,
                        Mode = i.Mode,
                        NextUpdate = DateTimeOffset.UtcNow,
                    }).ToList();
                    if (toSchedule.Count > 0)
                    {
                        _logger.LogInformation($"Adding {toSchedule.Count} items to schedule.");
                        db1.UpdateSchedules.AddRange(toSchedule);
                        await db1.SaveChangesAsync().ConfigureAwait(false);
                    }
                }

                await using var db = _dbContextFactory.CreateDbContext();
                int scheduledCount = await db.UpdateSchedules.CountAsync(s => s.NextUpdate <= DateTimeOffset.UtcNow).ConfigureAwait(false);
                var toUpdate = await db.UpdateSchedules
                    .Where(s => s.NextUpdate <= DateTimeOffset.UtcNow)
                    .OrderBy(s => s.NextUpdate)
                    .Take(200)
                    .ToListAsync().ConfigureAwait(false);
                _logger.LogInformation($"Updating {toUpdate.Count} of {scheduledCount} snapshots.");
                int successCount = 0;
                foreach (var schedule in toUpdate)
                {
                    try
                    {
                        await _dataMaintainer.UpdateAsync(schedule.UserId, schedule.Mode).ConfigureAwait(false);
                        schedule.NextUpdate = DateTimeOffset.UtcNow + TimeSpan.FromHours(6);
                        successCount++;
                    }
                    catch (Exception e)
                    {
                        _logger.LogInformation(e, $"Update error on user id {schedule.UserId} mode {schedule.Mode}");
                        if (e.Message.Contains("429 Too Many Requests"))
                            break;
                    }
                }
                await db.SaveChangesAsync().ConfigureAwait(false);
                _logger.LogInformation($"Update schedule completed. Success {successCount} of {toUpdate.Count}");
            }
            finally
            {
                int wasRunning = Interlocked.Exchange(ref s_running, 0);
                Debug.Assert(wasRunning == 1);
            }
        }
    }
#nullable restore
}
