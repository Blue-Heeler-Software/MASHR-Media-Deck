using System.Net;

namespace Mashr.MediaDeck.Linux;

public static class SecurityPolicy
{
    public const int CompanionPort = 43821;
    public const int DiscoveryPort = 43822;
    public const bool NetworkTransportEnabled = false;

    public static bool IsSafeScaffoldBind(IPAddress address) => IPAddress.IsLoopback(address);
}

