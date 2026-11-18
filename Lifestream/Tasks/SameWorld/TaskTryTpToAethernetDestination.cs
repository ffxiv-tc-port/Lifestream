using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.ChatMethods;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using Lifestream.Schedulers;
using Lifestream.Services;
using Lifestream.Systems;
using Lifestream.Systems.Legacy;
using static FFXIVClientStructs.FFXIV.Client.UI.AddonAirShipExploration;
using static System.Net.Mime.MediaTypeNames;

namespace Lifestream.Tasks.SameWorld;

internal static class TaskTryTpToAethernetDestination
{
    /// <summary>傳送後備遇到暫時性不能傳送時最多等多久；要短於任務逾時（30 秒），等不到就照舊報錯。</summary>
    private const long TpFallbackWaitMs = 10000;

    /// <summary>
    /// 站在最佳威兔洞（175）旁時，<paramref name="targetName"/> 是渴望灣或行星法恩娜（英文名或在地化名的子字串）就回它的偽 id，否則 0。
    /// 渴望灣先比：兩者都中的輸入維持舊行為（進渴望灣）。各自只在對應開關開著時比。
    /// </summary>
    internal static uint MatchCosmicGatewayName(string targetName, bool sinusArdorum, bool phaenna, string sinusArdorumLocalized, string phaennaLocalized)
    {
        if(sinusArdorum && ((string[])["Sinus Ardorum", sinusArdorumLocalized]).Any(n => n != null && n.ContainsAny(StringComparison.OrdinalIgnoreCase, targetName)))
            return TaskAetheryteAethernetTeleport.SinusArdorumAethernetId;
        if(phaenna && ((string[])["Phaenna", phaennaLocalized]).Any(n => n != null && n.ContainsAny(StringComparison.OrdinalIgnoreCase, targetName)))
            return TaskAetheryteAethernetTeleport.PhaennaAethernetId;
        return 0;
    }

    /// <summary>
    /// 回 true＝這一層已有結論、不再往下找（部分比對／傳送後備）：找到時 <paramref name="name"/> 為目的地名；
    /// 部分比對命中多項時為 null（已提示、不傳送，免得落到付費傳送的模糊比對）。
    /// </summary>
    private static bool TryPickLocalDestination<T>(IEnumerable<T> aetherytes, Func<T, string> nameOf, string targetName, out string name)
    {
        if(Utils.TryFindEqualsOrUniquePartial(aetherytes, nameOf, [targetName], out var hit, out var ambiguous))
        {
            name = nameOf(hit);
            return true;
        }
        name = null;
        if(ambiguous == null) return false;
        ChatQueue.Orange($"[Lifestream] {"?? has no exact match and partially matches ?? aethernet destinations (??), so no teleport was made. Please enter the full name.".Loc(targetName, ambiguous.Length, string.Join(" / ", ambiguous))}");
        return true;
    }

    public static void Enqueue(string targetName, bool allowPartial = false, bool allowTpFallback = false)
    {
        var tpWaitStartedAt = -1L;
        TaskManagerTask[] waiters = [new(WorldChange.WaitUntilMasterAetheryteExists), new FrameDelayTask(10), new(process)];
        if(C.WaitForScreenReady) P.TaskManager.Enqueue(Utils.WaitForScreen);
        if(P.ActiveAetheryte != null)
        {
            P.TaskManager.Enqueue(process);
        }
        // 本地網路裡沒有這個目的地（或區域表查不到）時不能停在這裡，要落到下面的部分比對／傳送後備。
        else if(S.Data.CustomAethernet.ActiveAetheryte != null
            && S.Data.CustomAethernet.ZoneInfo.TryGetValue(P.Territory, out var customZone)
            && TryPickLocalDestination(customZone.Aetherytes, x => x.Name, targetName, out var customDestination))
        {
            if(customDestination != null && customDestination.Contains(targetName, StringComparison.OrdinalIgnoreCase))
            {
                TaskAethernetTeleport.Enqueue(customDestination);
            }
        }
        else if(S.Data.ResidentialAethernet.ActiveAetheryte != null
            && S.Data.ResidentialAethernet.ZoneInfo.TryGetValue(S.Data.ResidentialAethernet.ActiveAetheryte.Value.TerritoryType, out var residentialZone)
            && TryPickLocalDestination(residentialZone.Aetherytes, x => x.Name, targetName, out var residentialDestination))
        {
            if(residentialDestination != null && residentialDestination.Contains(targetName, StringComparison.OrdinalIgnoreCase))
            {
                TaskAethernetTeleport.Enqueue(residentialDestination);
            }
        }
        else
        {
            P.TaskManager.Enqueue(() =>
            {
                if(P.ActiveAetheryte == null && Utils.GetReachableWorldChangeAetheryte() != null)
                {
                    P.TaskManager.InsertMulti([
                        new FrameDelayTask(10),
                        new(WorldChange.TargetReachableWorldChangeAetheryte),
                        new(WorldChange.LockOn),
                        new(WorldChange.EnableAutomove),
                        new(WorldChange.WaitUntilMasterAetheryteExists),
                        new(WorldChange.DisableAutomove),
                        ..waiters
                        ]);
                }
                else if(P.ActiveAetheryte == null)
                {
                    if(allowPartial && processPartial())
                    {
                        return;
                    }
                    if(allowTpFallback)
                    {
                        P.TaskManager.Insert(teleportFallback);
                    }
                    else
                    {
                        DuoLog.Error("Destination could not be found");
                    }
                }
                return;
            }, $"ConditionalLockonTask");

        }

        // 框架執行緒上的任務。暫時性的不能傳送（沒有角色、動畫鎖、移動／詠唱中等）最多等 TpFallbackWaitMs，
        // 不當成「找不到目的地」；其他拒絕與等太久都照舊交給 EnqueueTeleport（記錄原因並在聊天欄報錯）。
        bool teleportFallback()
        {
            if(!TeleportService.CanTeleport(out var err, out var status) && (status == 0 || TeleportRefusalWatch.IsTransient(status)))
            {
                var now = Environment.TickCount64;
                if(tpWaitStartedAt < 0) tpWaitStartedAt = now;
                if(now - tpWaitStartedAt < TpFallbackWaitMs)
                {
                    if(TravelDiag.ShouldLog($"TpFallback.wait.{status}"))
                    {
                        PluginLog.Information($"[Teleport] 目的地「{targetName}」改用傳送，先等暫時狀態解除：{err}。");
                    }
                    return false;
                }
            }
            if(!Utils.EnqueueTeleport(targetName, null))
            {
                DuoLog.Error("Destination could not be found");
            }
            return true;
        }

        bool processPartial()
        {
            PluginLog.Debug($"Processing partial command");
            foreach(var x in S.Data.DataStore.Aetherytes)
            {
                foreach(var a in (TinyAetheryte[])[x.Key, .. x.Value])
                {
                    if(a.Name.Contains(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        ChatQueue.Green($"[Lifestream] Destination: {ExcelTerritoryHelper.GetName(x.Key.TerritoryType)} - {a.Name}");
                        P.TaskManager.BeginStack();
                        try
                        {
                            TaskAetheryteAethernetTeleport.Enqueue(x.Key.ID, a.ID);
                        }
                        catch(Exception e)
                        {
                            e.Log();
                        }
                        P.TaskManager.InsertStack();
                        return true;
                    }
                }
            }
            return false;
        }

        void process()
        {
            var master = Utils.GetMaster();
            {
                if(P.ActiveAetheryte != master)
                {
                    var name = master.Name;
                    if(name.ContainsAny(StringComparison.OrdinalIgnoreCase, targetName) || C.Renames.TryGetValue(master.ID, out var value) && value.ContainsAny(StringComparison.OrdinalIgnoreCase, targetName))
                    {
                        P.TaskManager.BeginStack();
                        TaskRemoveAfkStatus.Enqueue();
                        TaskAethernetTeleport.Enqueue(master);
                        P.TaskManager.InsertStack();
                        return;
                    }
                }
            }

            foreach(var x in S.Data.DataStore.Aetherytes[master])
            {
                if(P.ActiveAetheryte != x)
                {
                    var name = x.Name;
                    if(name.ContainsAny(StringComparison.OrdinalIgnoreCase, targetName) || C.Renames.TryGetValue(x.ID, out var value) && value.ContainsAny(StringComparison.OrdinalIgnoreCase, targetName))
                    {
                        P.TaskManager.BeginStack();
                        TaskRemoveAfkStatus.Enqueue();
                        TaskAethernetTeleport.Enqueue(x);
                        P.TaskManager.InsertStack();
                        return;
                    }
                }
            }

            if(P.ActiveAetheryte.Value.ID == 70 && C.Firmament)
            {
                // 也接受在地化名稱(例如 /li 蒼天街)
                if(((string[])["Firmament", "Firmament".Loc()]).Any(n => n.ContainsAny(StringComparison.OrdinalIgnoreCase, targetName)))
                {
                    P.TaskManager.InsertStack(() =>
                    {
                        TaskRemoveAfkStatus.Enqueue();
                        TaskFirmanentTeleport.Enqueue();
                    });
                    return;
                }
            }

            // 比照蒼天街:站在「最佳威兔洞」(175)旁時 /li 渴望灣、/li 法恩娜 直達
            if(P.ActiveAetheryte.Value.ID == TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId && (C.SinusArdorum || C.Phaenna))
            {
                var gateway = MatchCosmicGatewayName(targetName, C.SinusArdorum, C.Phaenna, "Sinus Ardorum".Loc(), "Phaenna".Loc());
                if(gateway == TaskAetheryteAethernetTeleport.SinusArdorumAethernetId)
                {
                    P.TaskManager.InsertStack(() =>
                    {
                        TaskRemoveAfkStatus.Enqueue();
                        TaskSinusArdorumTeleport.Enqueue();
                    });
                    return;
                }
                if(gateway == TaskAetheryteAethernetTeleport.PhaennaAethernetId)
                {
                    // 走玄關路線（含未解鎖判定、沒到就中止佇列）；離席移除由該流程自己排。
                    PluginLog.Information($"[Gateway] 「{targetName}」比對到行星法恩娜，走最佳威兔洞的選單。");
                    P.TaskManager.InsertStack(() => TaskAetheryteAethernetTeleport.Enqueue(TaskAetheryteAethernetTeleport.SinusArdorumRootAetheryteId, TaskAetheryteAethernetTeleport.PhaennaAethernetId));
                    return;
                }
            }

            if(allowPartial && processPartial())
            {
                return;
            }

            if(allowTpFallback)
            {
                P.TaskManager.Insert(teleportFallback);
                return;
            }

            Notify.Error($"No destination {targetName} found");
            return;
        }
    }
}
