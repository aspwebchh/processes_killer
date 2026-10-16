using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ProcessesKiller
{
    internal sealed class ProcessTarget
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string NormalizedName { get; set; }
        public long? StartTimeUtcTicks { get; set; }
        public bool RequireIdentity { get; set; }
    }

    internal sealed class TerminationResult
    {
        public int Requested, Success, Skipped, NotExited;
        public readonly List<string> Failures = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public string Summary
        {
            get { return string.Format("结束完成：请求 {0} 个，成功 {1} 个，跳过 {2} 个，未退出 {3} 个，失败 {4} 个。", Requested, Success, Skipped, NotExited, Failures.Count); }
        }
    }

    internal static class ProcessTerminationService
    {
        // Call on a worker thread. The optional check revalidates port ownership immediately before Kill.
        public static TerminationResult Terminate(IEnumerable<ProcessTarget> targets, int currentProcessId,
            CancellationToken cancellationToken, Func<ProcessTarget, string> validateOwnership = null)
        {
            var uniqueTargets = targets.GroupBy(target => target.Id).Select(group => group.First()).ToList();
            var result = new TerminationResult { Requested = uniqueTargets.Count };
            foreach (var target in uniqueTargets)
            {
                string skipReason = null;
                var label = target.Name + " (PID " + target.Id + ")";
                if (cancellationToken.IsCancellationRequested) skipReason = "操作已停止";
                else if (target.Id == currentProcessId || target.Id <= 4) skipReason = "当前工具或系统进程不可结束";
                else if (target.RequireIdentity && !target.StartTimeUtcTicks.HasValue) skipReason = "无法验证进程身份";
                if (skipReason != null)
                {
                    Skip(result, label, skipReason);
                    continue;
                }
                try
                {
                    using (var process = Process.GetProcessById(target.Id))
                    {
                        // Cache a handle, so later PID reuse cannot redirect Kill to another process.
                        var handle = process.Handle;
                        if (process.HasExited) { Skip(result, label, "进程已退出"); continue; }
                        if (!string.Equals(process.ProcessName, target.NormalizedName, StringComparison.OrdinalIgnoreCase)
                            || (target.StartTimeUtcTicks.HasValue && process.StartTime.ToUniversalTime().Ticks != target.StartTimeUtcTicks.Value))
                        {
                            Skip(result, label, "进程身份已变化"); continue;
                        }
                        skipReason = validateOwnership == null ? null : validateOwnership(target);
                        if (skipReason != null) { Skip(result, label, skipReason); continue; }
                        if (cancellationToken.IsCancellationRequested) { Skip(result, label, "操作已停止"); continue; }
                        process.Kill();
                        if (process.WaitForExit(3000)) result.Success++;
                        else { result.NotExited++; result.Notes.Add(label + "：3 秒内尚未退出"); }
                    }
                }
                catch (ArgumentException) { Skip(result, label, "进程已退出"); }
                catch (InvalidOperationException) { Skip(result, label, "进程已退出或不可用"); }
                catch (Win32Exception ex)
                {
                    result.Failures.Add(label + "：" + (ex.NativeErrorCode == 5 ? "权限不足，可使用管理员身份运行后重试。" : ex.Message));
                }
                catch (Exception ex) { result.Failures.Add(label + "：" + ex.Message); }
            }
            return result;
        }

        private static void Skip(TerminationResult result, string label, string reason)
        {
            result.Skipped++;
            result.Notes.Add(label + "：" + reason);
        }
    }
}
