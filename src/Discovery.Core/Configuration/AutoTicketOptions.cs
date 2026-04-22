namespace Discovery.Core.Configuration;

public class AutoTicketOptions
{
    public const string SectionName = "AutoTicket";

    public bool Enabled { get; set; } = false;
    public bool ShadowMode { get; set; } = true;
    public int ReopenWindowMinutes { get; set; } = 0;
    public int MaxCreatedTicketsPerHourPerAlertCode { get; set; } = 0;
    public string[] CanaryClientIds { get; set; } = [];
    public string[] CanarySiteIds { get; set; } = [];
}