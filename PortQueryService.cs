using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace ProcessesKiller
{
    internal enum PortProtocolFilter { All, Tcp, Udp }

    internal sealed class PortEndpoint
    {
        public string Protocol { get; set; }
        public string IpVersion { get; set; }
        public string LocalAddress { get; set; }
        public int Port { get; set; }
        public int ProcessId { get; set; }
        public string State { get { return Protocol == "TCP" ? "监听" : "已绑定"; } }
        public string Key
        {
            get { return string.Join("|", Protocol, IpVersion, LocalAddress, Port.ToString(CultureInfo.InvariantCulture), ProcessId.ToString(CultureInfo.InvariantCulture)); }
        }
    }

    internal sealed class PortQueryResult
    {
        public readonly List<PortEndpoint> Endpoints = new List<PortEndpoint>();
        public readonly List<string> Errors = new List<string>();
        public int SuccessfulTables { get; set; }
        public bool IsComplete { get { return Errors.Count == 0; } }
    }

    /// <summary>Reads local socket ownership directly; no command output or locale-dependent parsing.</summary>
    internal sealed class PortQueryService
    {
        private readonly Func<bool, bool, List<PortEndpoint>> _readTable;

        public PortQueryService() : this(ReadNativeTable) { }

        internal PortQueryService(Func<bool, bool, List<PortEndpoint>> readTable)
        {
            if (readTable == null) throw new ArgumentNullException("readTable");
            _readTable = readTable;
        }

        internal static bool TryParsePort(string input, out int port)
        {
            return int.TryParse((input ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
                && port >= 1 && port <= 65535;
        }

        public PortQueryResult Query(int? port, PortProtocolFilter filter)
        {
            if (port.HasValue && (port.Value < 1 || port.Value > 65535)) throw new ArgumentOutOfRangeException("port");
            if (filter < PortProtocolFilter.All || filter > PortProtocolFilter.Udp) throw new ArgumentOutOfRangeException("filter");
            var result = new PortQueryResult();
            foreach (var tcp in new[] { true, false })
            {
                if ((tcp && filter == PortProtocolFilter.Udp) || (!tcp && filter == PortProtocolFilter.Tcp)) continue;
                foreach (var ipv6 in new[] { false, true })
                {
                    try
                    {
                        result.Endpoints.AddRange(_readTable(tcp, ipv6).Where(endpoint => !port.HasValue || endpoint.Port == port.Value));
                        result.SuccessfulTables++;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add((tcp ? "TCP" : "UDP") + (ipv6 ? " IPv6：" : " IPv4：") + ex.Message);
                    }
                }
            }
            var ordered = result.Endpoints.GroupBy(endpoint => endpoint.Key).Select(group => group.First())
                .OrderBy(endpoint => endpoint.Protocol).ThenBy(endpoint => endpoint.IpVersion)
                .ThenBy(endpoint => endpoint.LocalAddress).ThenBy(endpoint => endpoint.ProcessId).ToList();
            result.Endpoints.Clear();
            result.Endpoints.AddRange(ordered);
            return result;
        }

        internal string ValidateOwnership(int? port, PortProtocolFilter filter, int processId, ISet<string> selectedEndpointKeys)
        {
            var current = Query(port, filter);
            if (!current.IsComplete) return "无法完整验证端口归属，已跳过";
            return current.Endpoints.Any(endpoint => endpoint.ProcessId == processId && selectedEndpointKeys.Contains(endpoint.Key))
                ? null : "已不再占用选中的端口地址";
        }

        private static List<PortEndpoint> ReadNativeTable(bool tcp, bool ipv6)
        {
            var size = 0;
            var status = GetTable(tcp, ipv6, IntPtr.Zero, ref size);
            if (status == 0 && size == 0) return new List<PortEndpoint>();
            if (status != 0 && status != 122) throw new Win32Exception((int)status);
            // Socket tables can grow between the size query and the data query.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (size < 4 || size > 64 * 1024 * 1024) throw new InvalidOperationException("端口表大小异常。");
                var allocatedSize = size;
                var buffer = Marshal.AllocHGlobal(allocatedSize);
                try
                {
                    status = GetTable(tcp, ipv6, buffer, ref size);
                    if (status == 122) continue;
                    if (status != 0) throw new Win32Exception((int)status);
                    return ParseTable(buffer, Math.Min(size, allocatedSize), tcp, ipv6);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidOperationException("端口表持续变化，请重试。");
        }

        private static uint GetTable(bool tcp, bool ipv6, IntPtr buffer, ref int size)
        {
            // TCP_TABLE_OWNER_PID_LISTENER = 3; UDP_TABLE_OWNER_PID = 1.
            return tcp ? GetExtendedTcpTable(buffer, ref size, false, ipv6 ? 23 : 2, 3, 0)
                : GetExtendedUdpTable(buffer, ref size, false, ipv6 ? 23 : 2, 1, 0);
        }

        internal static List<PortEndpoint> ParseTable(IntPtr buffer, int size, bool tcp, bool ipv6)
        {
            if (size < 4) throw new InvalidOperationException("端口表数据不完整。");
            var rowType = tcp ? (ipv6 ? typeof(Tcp6Row) : typeof(Tcp4Row)) : (ipv6 ? typeof(Udp6Row) : typeof(Udp4Row));
            var stride = Marshal.SizeOf(rowType);
            var count = Marshal.ReadInt32(buffer);
            if (count < 0 || count > (size - 4) / stride) throw new InvalidOperationException("端口表行数异常。");
            var endpoints = new List<PortEndpoint>();
            for (var index = 0; index < count; index++)
            {
                var address = IntPtr.Add(buffer, 4 + index * stride);
                uint nativePort, pid;
                string localAddress;
                if (tcp && ipv6)
                {
                    var row = (Tcp6Row)Marshal.PtrToStructure(address, rowType);
                    if (row.State != 2) continue;
                    nativePort = row.LocalPort; pid = row.Pid;
                    localAddress = FormatIpv6(row.LocalAddress, row.LocalScope);
                }
                else if (tcp)
                {
                    var row = (Tcp4Row)Marshal.PtrToStructure(address, rowType);
                    if (row.State != 2) continue;
                    nativePort = row.LocalPort; pid = row.Pid;
                    localAddress = new IPAddress(BitConverter.GetBytes(row.LocalAddress)).ToString();
                }
                else if (ipv6)
                {
                    var row = (Udp6Row)Marshal.PtrToStructure(address, rowType);
                    nativePort = row.LocalPort; pid = row.Pid;
                    localAddress = FormatIpv6(row.LocalAddress, row.LocalScope);
                }
                else
                {
                    var row = (Udp4Row)Marshal.PtrToStructure(address, rowType);
                    nativePort = row.LocalPort; pid = row.Pid;
                    localAddress = new IPAddress(BitConverter.GetBytes(row.LocalAddress)).ToString();
                }
                if (pid > int.MaxValue) throw new InvalidOperationException("进程 ID 超出范围。");
                endpoints.Add(new PortEndpoint
                {
                    Protocol = tcp ? "TCP" : "UDP", IpVersion = ipv6 ? "IPv6" : "IPv4",
                    LocalAddress = localAddress, Port = DecodePort(nativePort), ProcessId = (int)pid
                });
            }
            return endpoints;
        }

        internal static int DecodePort(uint value) { return (int)(((value & 255) << 8) | ((value >> 8) & 255)); }

        private static string FormatIpv6(byte[] address, uint scope)
        {
            var hostScope = (uint)IPAddress.NetworkToHostOrder(unchecked((int)scope));
            return new IPAddress(address, hostScope).ToString();
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Tcp4Row { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Udp4Row { public uint LocalAddress, LocalPort, Pid; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Tcp6Row
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
            public uint LocalScope, LocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
            public uint RemoteScope, RemotePort, State, Pid;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Udp6Row
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
            public uint LocalScope, LocalPort, Pid;
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    }
}
