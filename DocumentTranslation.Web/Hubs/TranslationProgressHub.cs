using Microsoft.AspNetCore.SignalR;

namespace DocumentTranslation.Web.Hubs
{
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