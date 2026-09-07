using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.Development;

namespace AIWhisper.Worker.Persistence;

public sealed class CampaignStateSnapshot
{
    public CampaignMemory Memory { get; set; } = new();
    public ParasiteDevelopmentState Development { get; set; } = new();
    public SessionContext Session { get; set; } = new();
}
