using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LocalStreamPC.Services
{
    public static class NetworkHelper
    {
        /// <summary>IPv4 addresses of the network adapters that are up (no loopback, virtual or APIPA).</summary>
        public static List<(string Ip, string Name)> GetActiveIPv4Addresses()
        {
            var result = new List<(string Ip, string Name)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                string desc = nic.Description.ToLowerInvariant();
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    desc.Contains("virtual") || desc.Contains("pseudo"))
                    continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue; // APIPA: no DHCP, unreachable
                    result.Add((ip, nic.Name));
                }
            }
            if (result.Count == 0) result.Add(("127.0.0.1", "Localhost"));
            return result;
        }

        /// <summary>Tailscale gives each device an address in 100.64.0.0/10 that works from anywhere.</summary>
        public static bool IsTailscale(string ip)
            => IPAddress.TryParse(ip, out var a) && a.GetAddressBytes() is { Length: 4 } b && b[0] == 100 && b[1] >= 64 && b[1] <= 127;

        /// <summary>This PC's Tailscale address, if Tailscale is installed and connected.</summary>
        public static string? GetTailscaleAddress()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork && IsTailscale(addr.Address.ToString())) return addr.Address.ToString();
                }
            }
            catch { }
            return null;
        }

        /// <summary>The address of the adapter that routes to the internet (no packets are sent).</summary>
        public static string GetPrimaryIPv4()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
            }
            catch { return "127.0.0.1"; }
        }

        /// <summary>A LAN client, or this PC itself (remote address == the address it connected to).</summary>
        public static bool IsLocalClient(IPAddress? remote, IPAddress? local)
        {
            if (IsPrivateAddress(remote)) return true;
            return remote != null && local != null && remote.Equals(local);
        }

        /// <summary>
        /// True for loopback, RFC 1918 (10/8, 172.16/12, 192.168/16), CGNAT 100.64/10,
        /// link-local, and IPv6 unique-local addresses - i.e. clients on the local network.
        /// </summary>
        public static bool IsPrivateAddress(IPAddress? address)
        {
            if (address == null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return true;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] b = address.GetAddressBytes();
                return address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC; // fe80::/10, fc00::/7
            }
            return false;
        }
    }
}
