namespace Ventrail.Models;

public class PeerNode
{
    public string PeerId { get; set; } = string.Empty;
    public string IPAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool IsConnected { get; set; }
}