using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;

namespace PickupTrackerServer;

/// <summary>
/// Hook 宿主：挂在 MatchController.EndLocalRaidAsync 的 **Prefix** 上。
///
/// 为什么不是 MoveItem：实测（2026-10-10 日志逐条核对）局内拾取不经过服务端，
/// raid 期间服务端收不到任何物品移动请求；客户端只在结束对局时把完整 profile 回传。
///
/// Prefix 的时机正好在服务端应用客户端数据之前 —— 此时磁盘/内存里的 profile 还是 raid 前状态，
/// 可以用来 diff 出本局新到手的物品。全程只读，返回值 void，不影响原方法行为。
///
/// 参数名必须与目标方法签名一致：
///   MatchController.EndLocalRaidAsync(MongoId sessionId, EndLocalRaidRequestData request, CancellationToken cancellationToken)
/// </summary>
public static class EndRaidPatch
{
    public static void Prefix(MongoId sessionId, EndLocalRaidRequestData request)
    {
        PickupRecorder.OnRaidEnded(sessionId, request);
    }
}
