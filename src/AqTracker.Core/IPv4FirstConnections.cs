using System.Net;
using System.Net.Sockets;

namespace AqTracker.Core;

/// <summary>
/// .NET Framework connects to a host's addresses one by one in DNS order (IPv6 first) and only moves on after the
/// ~21 s TCP connect timeout, so an advertised but unreachable IPv6 route stalls every new connection. Refusing IPv6
/// endpoints at bind time sends the connection straight to the next (IPv4) address; callers switch back to IPv6
/// when IPv4 itself stops working, so IPv6-only networks keep working.
/// </summary>
public static class IPv4FirstConnections
{
    public static IPEndPoint? SkipIPv6(ServicePoint servicePoint, IPEndPoint remoteEndPoint, int retryCount) =>
        remoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? throw new SocketException((int)SocketError.AddressFamilyNotSupported)
            : null;

    // Applied before every request: idle service points are purged and recreated without the delegate.
    public static void Apply(Uri uri, bool skipIPv6) =>
        ServicePointManager.FindServicePoint(uri).BindIPEndPointDelegate = skipIPv6 ? SkipIPv6 : null;
}
