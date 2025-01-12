﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bleatingsheep.NewHydrant.Attributions;
using Bleatingsheep.NewHydrant.Data;
using Bleatingsheep.Osu;
using Bleatingsheep.Osu.ApiClient;
using Bleatingsheep.OsuQqBot.Database.Models;
using Microsoft.EntityFrameworkCore;
using Sisters.WudiLib;
using Sisters.WudiLib.Posts;
using Message = Sisters.WudiLib.SendingMessage;
using MessageContext = Sisters.WudiLib.Posts.Message;
using Mode = Bleatingsheep.Osu.Mode;

namespace Bleatingsheep.NewHydrant.Osu.Yearly
{
#nullable enable
    [Component("MyYearly")]
    public partial class MyYearly : IMessageCommand
    {
        private readonly IDataProvider _dataProvider;
        private readonly IOsuApiClient _osuApiClient;
        private readonly NewbieContext _newbieContext;
        private readonly TimeSpan _timeZone = TimeSpan.FromHours(8);
        // private static readonly TimeZoneInfo _timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

        private Mode _mode = Mode.Standard;
        private IReadOnlyList<UserPlayRecord> _userPlayRecords = default!;
        private IReadOnlyDictionary<int, BeatmapInfo> _beatmapInfoDict = default!;
        private bool _hasError = false;

        public MyYearly(IDataProvider dataProvider, IOsuApiClient osuApiClient, NewbieContext newbieContext)
        {
            _dataProvider = dataProvider;
            _osuApiClient = osuApiClient;
            _newbieContext = newbieContext;
        }

        private string ModeString { get; set; } = default!;

        public async Task ProcessAsync(MessageContext context, HttpApiClient api)
        {
            var startDate = new DateTimeOffset(2024, 12, 31, 17, 0, 0, _timeZone);
            var endDate = new DateTimeOffset(2025, 2, 13, 0, 0, 0, _timeZone);
            if (!(context is GroupMessage g && g.GroupId == 695600319)) // 允许管理群测试
            {
                if (DateTimeOffset.UtcNow < startDate)
                {
                    await api.SendMessageAsync(context.Endpoint, "活动未开始。");
                    return;
                }
                if (DateTimeOffset.UtcNow >= endDate)
                {
                    await api.SendMessageAsync(context.Endpoint, "活动已结束。");
                    return;
                }
            }

            // check binding
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset start = now.AddYears(-1);
            int? osuId = await _dataProvider.GetOsuIdAsync(context.UserId).ConfigureAwait(false);
            if (osuId == null)
            {
                _ = await api.SendMessageAsync(context.Endpoint, "未绑定").ConfigureAwait(false);
                return;
            }

            // apply mode
            try
            {
                if (!string.IsNullOrWhiteSpace(ModeString))
                {
                    _mode = ModeExtensions.Parse(ModeString);
                }
            }
            catch
            {
                await api.SendMessageAsync(context.Endpoint, $"未知游戏模式{ModeString}。将生成 std 模式的报告。").ConfigureAwait(false);
            }

            // retrieve data from local snapshots.
            UserSnapshot? snap = await _newbieContext.UserSnapshots
                .Where(s => s.UserId == osuId && s.Mode == _mode && s.Date > start)
                .OrderBy(s => s.Date)
                .FirstOrDefaultAsync().ConfigureAwait(false);
            if (snap is null)
            {
                _ = await api.SendMessageAsync(context.Endpoint, "没有找到快照数据。").ConfigureAwait(false);
                return;
            }

            // get current state
            UserInfo? userInfo = default;
            try
            {
                userInfo = await _osuApiClient.GetUser(osuId.Value, _mode).ConfigureAwait(false);
            }
            catch
            {
                // ignore, use local data
                _hasError = true;
            }
            if (userInfo is null)
            {
                // user may be banned
                userInfo = (await _newbieContext.UserSnapshots
                    .Where(s => s.UserId == osuId && s.Mode == _mode)
                    .OrderByDescending(s => s.Date)
                    .FirstAsync().ConfigureAwait(false)).UserInfo;
            }
            int startPC = snap.UserInfo.PlayCount;
            int currentPC = userInfo.PlayCount;
            List<UserPlayRecord> playList = await _newbieContext.UserPlayRecords
                .Where(r => r.UserId == osuId && r.Mode == _mode && r.PlayNumber > startPC)
                .OrderBy(r => r.Record.Date)
                .ToListAsync().ConfigureAwait(false);

            if (playList.Count == 0)
            {
                await api.SendMessageAsync(context.Endpoint, "你在过去一年没有玩儿过 osu!，或无游玩数据记录。").ConfigureAwait(false);
                return;
            }

            // assign data to fields.
            _userPlayRecords = playList;
            {
                // first response
                var frsb = new StringBuilder();
                frsb.AppendLine($"{userInfo.Name}。当前模式：{_mode}，数据始于{snap.Date.ToOffset(_timeZone):M月d日}，完整度：{playList.Count}/{currentPC - startPC}。");
                {
                    // days played
                    (int days, int totalDays) = GetPlayedDays();
                    frsb.Append($"你在过去一年中有 {days} 天打了图，合计 {userInfo.PlayCount - snap.UserInfo.PlayCount} 次，{userInfo.TotalHits - snap.UserInfo.TotalHits} TTH，{(userInfo.PlayTime - snap.UserInfo.PlayTime).TotalHours:#.##} 小时。");
                    frsb.AppendLine($"增长了 {userInfo.Performance - snap.UserInfo.Performance:#.##}PP。");
                }
                frsb.Append("正在生成报告，请稍候。");
                _ = await api.SendMessageAsync(context.Endpoint, frsb.ToString()).ConfigureAwait(false);
            }

            // beatmap info
            var playedBeatmaps = playList.Select(r => r.Record.BeatmapId).Distinct().ToHashSet();
            var cachedBeatmapInfo = await _newbieContext.BeatmapInfoCache.Where(e => e.Mode == _mode && playedBeatmaps.Contains(e.BeatmapId)).ToListAsync().ConfigureAwait(false);
            var noCacheBeatmaps = playedBeatmaps.Except(cachedBeatmapInfo.Select(e => e.BeatmapId));
            var beatmapInfoList = cachedBeatmapInfo.Where(e => e.BeatmapInfo != null).Select(e => e.BeatmapInfo!).ToList();
            foreach (var beatmapId in noCacheBeatmaps)
            {
                try
                {
                    var current = await _dataProvider.GetBeatmapInfoAsync(beatmapId, _mode).ConfigureAwait(false);
                    if (current != null)
                    {
                        beatmapInfoList.Add(current);
                    }
                }
                catch
                {
                    _hasError = true;
                }
            }
            _beatmapInfoDict = beatmapInfoList.ToDictionary(bi => bi.Id);

            var sb = new StringBuilder();
            if (_hasError)
            {
                sb.AppendLine("由于请求量过高，有错误发生，数据可能不准确。");
            }
            {
                // most played
                (int bid, int count, BeatmapInfo? beatmap) = GetMostPlayedBeatmap();
                sb.AppendLine($"你最常打的一张图是 b/{bid}，打了 {count} 次。{beatmap}");
            }
            {
                // mods
                var (mods, count) = GetFavoriteMods();
                var modsString = mods.Display();
                if (string.IsNullOrEmpty(modsString))
                {
                    modsString = "None";
                }
                sb.AppendLine($"{modsString} 是你最喜欢的 mods，贡献了你 {(double)count / _userPlayRecords.Count:P0} 的游玩次数。");
            }
            {
                (string? favoriteMapperName, int favoriteMapperPlayCount) = GetFavoriteMapper();
                if (favoriteMapperName != null)
                {
                    sb.AppendLine($"{favoriteMapperName} 是你最喜欢的 mapper，打了她/他的图 {favoriteMapperPlayCount} 次。");
                }
            }
            {
                // most playing hour
                var mostPlayingHour = GetMostPlayingHours();
                sb.AppendLine($"{mostPlayingHour}-{mostPlayingHour + 1} 时是你最常打图的时间。");
            }
            {
                // most played beatmap of the day
                var (bid, date, count, fc) = GetMostPlayedBeatmapOfDay();
                if (bid != default)
                {
                    string fcString = fc == true
                        ? "全连了，真不容易。"
                        : fc == false
                        ? "都没全连，真菜。"
                        : string.Empty;
                    var beatmapInfo = _beatmapInfoDict.GetValueOrDefault(bid);
                    sb.AppendLine($"{date.ToShortDateString()}，你把 b/{bid} 挑战了 {count} 次。{fcString}{beatmapInfo}");
                }
            }
            {
                // longest continuous play
                var (start1, end, pc, tth) = GetLongestContinuousPlay(out var periods);
                if (start1 != end)
                {
                    sb.AppendLine($"{start1.ToOffset(_timeZone):M-d H:mm} 到 {end.ToOffset(_timeZone):M-d H:mm}，你连续打了 {pc} 次，{(end - start1).TotalHours:0.#} 小时，是你连续游玩时间最长的一次。");
                }
                if (periods.Count > 0)
                {
                    var (dtoff, isOvernight) = GetLatestPlay(periods);
                    var date = dtoff.Date;
                    var time = dtoff.TimeOfDay;
                    // var comment = time.Hours switch
                    // {
                    //     < 2 => "要注意休息。",
                    //     < 5 => "osu! 陪你度过不眠夜。",
                    //     < 18 => "大好的晚上不能浪费在 osu! 上。",
                    //     _ => "除了 osu!，你还有人生，健康作息很重要。",
                    // };
                    if (isOvernight)
                    {
                        sb.AppendLine($"{date.ToShortDateString()}，你通宵打图打到{time.Hours}点{time.Minutes}分，是最晚的一次。");
                    }
                    else
                    {
                        sb.AppendLine($"{date.ToShortDateString()}，你{time.Hours}点{time.Minutes}分还在打图，是最晚的一次。");
                    }
                }
            }
            sb.Append($"{userInfo.Name} 的年度 osu! 记录。");
            await api.SendMessageAsync(context.Endpoint, sb.ToString()).ConfigureAwait(false);
        }

        /// <summary>
        /// Get the number of days with play records.
        /// </summary>
        /// <returns></returns>
        private (int days, int totalDays) GetPlayedDays()
        {
            List<DateTime> days = _userPlayRecords.Select(r => r.Record.Date.Date).Distinct().ToList();
            return (days.Count, 365);
        }

        private (int bid, int count, BeatmapInfo? beatmapInfo) GetMostPlayedBeatmap()
        {
            IGrouping<int, UserPlayRecord>? mostPlayed = _userPlayRecords
                .GroupBy(r => r.Record.BeatmapId)
                .OrderByDescending(g => g.Count())
                .First();
            (int bid, int count) = (mostPlayed.Key, mostPlayed.Count());
            BeatmapInfo? beatmap = _beatmapInfoDict.GetValueOrDefault(bid);
            return (bid, count, beatmap);
        }

        private (Mods mods, int count) GetFavoriteMods()
        {
            var favModsGroup = _userPlayRecords.GroupBy(r => r.Record.EnabledMods).OrderByDescending(g => g.Count()).First();
            return (favModsGroup.Key, favModsGroup.Count());
        }

        private int GetMostPlayingHours()
        {
            int mostPlayingHour = _userPlayRecords
                .GroupBy(r => new DateTimeOffset(r.Record.Date).ToOffset(_timeZone).Hour)
                .OrderByDescending(_g => _g.Count())
                .First()
                .Key;
            return mostPlayingHour;
        }

        private (string? favoriteMapperName, int favoriteMapperPlayCount) GetFavoriteMapper()
        {
            var playedBeatmaps = _userPlayRecords.Select(r => r.Record.BeatmapId).Distinct().ToHashSet();
            var favoriteMapperId = playedBeatmaps.GroupBy(bid => _beatmapInfoDict.GetValueOrDefault(bid)?.CreatorId).OrderByDescending(g => g.Count()).FirstOrDefault(g => g.Key != default)?.Key;
            if (favoriteMapperId != null)
            {
                var favoriteMapperBeatmaps = _beatmapInfoDict.Values.Where(b => b.CreatorId == favoriteMapperId).ToList();
                var favoriteMapperName = favoriteMapperBeatmaps.OrderByDescending(b => b.ApprovedDate).FirstOrDefault()?.Creator;
                var favoriteMapperBeatmapIds = favoriteMapperBeatmaps.Select(b => b.Id).ToHashSet();
                var favoriteMapperPlayCount = _userPlayRecords.Count(r => favoriteMapperBeatmapIds.Contains(r.Record.BeatmapId));
                return (favoriteMapperName, favoriteMapperPlayCount);
            }
            return default;
        }

        private (int beatmapId, DateTime date, int count, bool? fullCombo) GetMostPlayedBeatmapOfDay()
        {
            var mostPlayedOfTheDay = _userPlayRecords
                .Where(r =>
                    r.Record.Date >= _beatmapInfoDict.GetValueOrDefault(r.Record.BeatmapId)?.LastUpdate
                    && _beatmapInfoDict.GetValueOrDefault(r.Record.BeatmapId)?.Approved is Approved.Approved or Approved.Qualified or Approved.Loved)
                .GroupBy(r =>
                {
                    var adjustedDate = new DateTimeOffset(r.Record.Date).ToOffset(_timeZone).AddHours(-5).Date;
                    return (r.Record.BeatmapId, adjustedDate);
                }).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (mostPlayedOfTheDay == null)
                return default;
            var (bid, date) = mostPlayedOfTheDay.Key;
            var beatmapInfo = _beatmapInfoDict.GetValueOrDefault(bid);
            bool? fullCombo = default;
            if (beatmapInfo != null)
            {
                var maxCombo = beatmapInfo.MaxCombo;
                fullCombo = mostPlayedOfTheDay.Any(r => r.Record.CountMiss == 0 && r.Record.Count100 + r.Record.Count50 > maxCombo - r.Record.MaxCombo);
            }
            return (bid, date, mostPlayedOfTheDay.Count(), fullCombo);
        }

        private (DateTimeOffset start, DateTimeOffset end, int pc, int tth) GetLongestContinuousPlay(out List<(DateTimeOffset start, DateTimeOffset end, int pc, int tth)> periods)
        {
            periods = new List<(DateTimeOffset start, DateTimeOffset end, int pc, int tth)>();
            var start = _userPlayRecords[0].Record.Date;
            int pc = 0;
            int tth = 0;
            var last = _userPlayRecords[0].Record.Date;
            foreach (var r in _userPlayRecords)
            {
                if (last.AddHours(2) < r.Record.Date)
                {
                    if (start != last)
                    {
                        periods.Add((start, last, pc, tth));
                    }
                    start = r.Record.Date;
                    pc = 0;
                    tth = 0;
                }
                pc++;
                tth += r.Record.Count300 + r.Record.Count100 + r.Record.Count50;
                last = r.Record.Date;
            }
            if (start != last)
            {
                periods.Add((start, last, pc, tth));
            }
            return periods.Count == 0 ? default : periods.MaxBy(t => t.end - t.start);
        }

        private (DateTimeOffset, bool isOvernight) GetLatestPlay(List<(DateTimeOffset start, DateTimeOffset end, int pc, int tth)> periods)
        {
            var mostNight = periods.OrderByDescending(t =>
            {
                var (start, end, _, _) = t;
                start = start.ToOffset(_timeZone);
                end = end.ToOffset(_timeZone);
                return IsOvernight(start, end)
                    ? end.TimeOfDay + TimeSpan.FromHours(24 - 5)
                    : t.end.AddHours(-5).ToOffset(_timeZone).TimeOfDay;
            }).First();
            return (mostNight.end.ToOffset(_timeZone), IsOvernight(mostNight.start.ToOffset(_timeZone), mostNight.end.ToOffset(_timeZone)));
        }

        private static bool IsOvernight(DateTimeOffset start, DateTimeOffset end)
        {
            TimeSpan am5 = TimeSpan.FromHours(5);
            TimeSpan noon = TimeSpan.FromHours(12);
            if (end.TimeOfDay < am5 || end.TimeOfDay >= noon)
            {
                // earlier than 5 AM.
                return false;
            }
            if (start.Date != end.Date)
            {
                // overnight?
                return true;
            }
            var am415 = new TimeSpan(4, 15, 0);
            if (start.TimeOfDay < am415 && end.TimeOfDay > am415)
            {
                // also maybe overnight
                // condition: period before 4:15am is longer than after 4:15am
                // otherwise, morning.
                var lengthBefore415 = am415 - start.TimeOfDay;
                var lengthAfter415 = end.TimeOfDay - am415;
                if (lengthBefore415 > lengthAfter415)
                {
                    return true;
                }
            }
            // morning
            return false;
        }

        [GeneratedRegex("^我的年度(?:屙屎|osu[!！]?)\\s*(?:[,，]\\s*(?<mode>.+?)\\s*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
        private static partial Regex MatchingRegex();
        private static readonly Regex s_regex = MatchingRegex();

        public bool ShouldResponse(MessageContext context)
        {
            if (!context.Content.TryGetPlainText(out string text))
            {
                return false;
            }
            var match = s_regex.Match(text.Trim());
            if (!match.Success)
            {
                return false;
            }
            var modeGroup = match.Groups["mode"];
            ModeString = modeGroup.Value;
            return true;
        }

        // TODO:
        // 最常打的 mod 是 None 时不显示（或者 > 50% 时不显示）
        // 年度谱面：打图次数最多的
        // 最容易刷出 PP 的时间段
        // 春天，你打 xx；夏天，你打 xx；秋天，你打 xx；冬天，你打 xx。
        // 打图年代分布图：你最常打 xx 年代的图（先把所有人的数据混一起，手动划分几个合适的时间段，再设计文案）
        // 你的打图品味超过 xx% 的用户（越小众超的人越多）【什么鬼
        // b/xx 是你曾经打了很多遍但没有 FC 的图，你似乎把它遗忘了。
    }
#nullable restore
}
