using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;

namespace DocumentTranslation.Web.Hubs
{
    [Authorize(Policy = "RequireTranslatorUser")]
    public class TranslationProgressHub : Hub
    {
        public async Task JoinTranslationGroup(string connectionId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"translation_{connectionId}");
        }

        public async Task LeaveTranslationGroup(string connectionId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"translation_{connectionId}");
        }
    }
}