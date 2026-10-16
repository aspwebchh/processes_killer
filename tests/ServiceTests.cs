using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using ProcessesKiller;

internal static class ServiceTests
{
    private static int _assertions;

    public static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--listener" || args[0] == "--udp-listener")) return RunListener(args);
        try
        {
            TestPortInput();
            TestNativeLayouts();
            TestPartialQueries();
            TestLiveOwnershipAndTermination();
            Console.WriteLine("PASS: " + _assertions + " assertions; all fixtures cleaned up.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        _assertions++;
    }

    private static void TestPortInput()
    {
        int port;
        foreach (var input in new[] { "", " ", "0", "65536", "-1", "+80", "1,2", "80a", "1.0", "１２", "2147483648" })
            Check(!PortQueryService.TryParsePort(input, out port), "reject invalid port: " + input);
        Check(PortQueryService.TryParsePort(" 65535 \r\n", out port) && port == 65535, "trim and upper boundary");
        Check(PortQueryService.TryParsePort("1", out port) && port == 1, "lower boundary");
        Check(PortQueryService.TryParsePort("08080", out port) && port == 8080, "leading zeroes");
        Console.WriteLine("PASS: port input validation");
    }

    private static uint EncodePort(int port) { return (uint)(((port & 255) << 8) | ((port >> 8) & 255)); }

    private static void TestNativeLayouts()
    {
        Check(Marshal.SizeOf(typeof(PortQueryService.Tcp4Row)) == 24, "TCP4 layout");
        Check(Marshal.SizeOf(typeof(PortQueryService.Tcp6Row)) == 56, "TCP6 layout");
        Check(Marshal.SizeOf(typeof(PortQueryService.Udp4Row)) == 12, "UDP4 layout");
        Check(Marshal.SizeOf(typeof(PortQueryService.Udp6Row)) == 28, "UDP6 layout");
        var ipv4 = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
        VerifyRow(new PortQueryService.Tcp4Row { State = 2, LocalAddress = ipv4, LocalPort = EncodePort(8080), Pid = 1234 }, true, false, "127.0.0.1");
        VerifyRow(new PortQueryService.Udp4Row { LocalAddress = ipv4, LocalPort = EncodePort(8080), Pid = 1234 }, false, false, "127.0.0.1");
        VerifyRow(new PortQueryService.Tcp6Row { State = 2, LocalAddress = IPAddress.IPv6Loopback.GetAddressBytes(),
            RemoteAddress = new byte[16], LocalPort = EncodePort(8080), Pid = 1234 }, true, true, "::1");
        VerifyRow(new PortQueryService.Udp6Row { LocalAddress = IPAddress.IPv6Loopback.GetAddressBytes(), LocalPort = EncodePort(8080), Pid = 1234 }, false, true, "::1");
        VerifyRow(new PortQueryService.Udp6Row { LocalAddress = IPAddress.Parse("fe80::1").GetAddressBytes(),
            LocalScope = unchecked((uint)IPAddress.HostToNetworkOrder(7)), LocalPort = EncodePort(8080), Pid = 1234 }, false, true, "fe80::1%7");
        var size = 4 + Marshal.SizeOf(typeof(PortQueryService.Tcp4Row));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, 1);
            Marshal.StructureToPtr(new PortQueryService.Tcp4Row { State = 5, LocalPort = EncodePort(8080) }, IntPtr.Add(buffer, 4), false);
            Check(PortQueryService.ParseTable(buffer, size, true, false).Count == 0, "exclude non-listening TCP");
            Marshal.WriteInt32(buffer, int.MaxValue);
            var rejected = false;
            try { PortQueryService.ParseTable(buffer, size, true, false); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "reject truncated table");
        }
        finally { Marshal.FreeHGlobal(buffer); }
        Check(PortQueryService.DecodePort(EncodePort(1)) == 1 && PortQueryService.DecodePort(EncodePort(65535)) == 65535, "port byte order boundaries");
        Console.WriteLine("PASS: native table layouts, state and byte order");
    }

    private static void VerifyRow<T>(T row, bool tcp, bool ipv6, string expectedAddress)
    {
        var size = 4 + Marshal.SizeOf(typeof(T));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, 1);
            Marshal.StructureToPtr(row, IntPtr.Add(buffer, 4), false);
            var endpoint = PortQueryService.ParseTable(buffer, size, tcp, ipv6).Single();
            Check(endpoint.Port == 8080 && endpoint.ProcessId == 1234 && endpoint.LocalAddress == expectedAddress,
                "parse " + typeof(T).Name + " " + expectedAddress);
            Check(endpoint.Protocol == (tcp ? "TCP" : "UDP") && endpoint.IpVersion == (ipv6 ? "IPv6" : "IPv4"), "endpoint protocol and family");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void TestPartialQueries()
    {
        var service = new PortQueryService((tcp, ipv6) =>
        {
            if (!tcp && ipv6) throw new Win32Exception(5);
            var endpoint = new PortEndpoint { Protocol = tcp ? "TCP" : "UDP", IpVersion = ipv6 ? "IPv6" : "IPv4",
                LocalAddress = ipv6 ? "::1" : "127.0.0.1", Port = 8080, ProcessId = 42 };
            return new List<PortEndpoint> { endpoint, endpoint,
                new PortEndpoint { Port = 9999, Protocol = "TCP", IpVersion = "IPv4", ProcessId = 42 } };
        });
        var all = service.Query(8080, PortProtocolFilter.All);
        Check(!all.IsComplete && all.SuccessfulTables == 3 && all.Errors.Count == 1, "partial table failure remains explicit");
        Check(all.Endpoints.Count == 3 && all.Endpoints.All(endpoint => endpoint.Port == 8080), "filter port and deduplicate endpoints");
        var tcpOnly = service.Query(8080, PortProtocolFilter.Tcp);
        Check(tcpOnly.IsComplete && tcpOnly.Endpoints.Count == 2 && tcpOnly.SuccessfulTables == 2, "TCP only does not query UDP");
        var failed = new PortQueryService((tcp, ipv6) => { throw new Win32Exception(5); }).Query(8080, PortProtocolFilter.All);
        Check(!failed.IsComplete && failed.SuccessfulTables == 0 && failed.Errors.Count == 4, "all tables failing is not a free port");
        var unfiltered = service.Query(null, PortProtocolFilter.All);
        Check(unfiltered.Endpoints.Any(endpoint => endpoint.Port == 8080) && unfiltered.Endpoints.Any(endpoint => endpoint.Port == 9999), "null port includes different ports");
        Check(!unfiltered.IsComplete, "all-port query preserves partial errors");
        var original = new PortEndpoint { Protocol = "TCP", IpVersion = "IPv4", LocalAddress = "127.0.0.1", Port = 8080, ProcessId = 42 };
        var replacement = new PortEndpoint { Protocol = "TCP", IpVersion = "IPv4", LocalAddress = "127.0.0.1", Port = 9999, ProcessId = 42 };
        var changed = new PortQueryService((tcp, ipv6) => tcp && !ipv6 ? new List<PortEndpoint> { replacement } : new List<PortEndpoint>());
        Check(changed.ValidateOwnership(null, PortProtocolFilter.All, 42, new HashSet<string> { original.Key }) != null, "other port on same PID cannot validate missing selected endpoint");
        Check(changed.ValidateOwnership(null, PortProtocolFilter.All, 42, new HashSet<string> { replacement.Key }) == null, "selected endpoint still owned validates");
        Check(service.ValidateOwnership(null, PortProtocolFilter.All, 42, new HashSet<string> { original.Key }) != null, "partial tables cannot validate ownership");
        var invalidPortRejected = false;
        try { changed.Query(0, PortProtocolFilter.All); } catch (ArgumentOutOfRangeException) { invalidPortRejected = true; }
        Check(invalidPortRejected, "zero is not an alias for all ports");
        Console.WriteLine("PASS: protocol filtering and partial failures");
    }

    private static ProcessTarget Target(Process process)
    {
        return new ProcessTarget { Id = process.Id, Name = process.ProcessName + ".exe", NormalizedName = process.ProcessName,
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks, RequireIdentity = true };
    }

    private static void TestLiveOwnershipAndTermination()
    {
        int port;
        using (var fixture = StartFixture(0, out port))
        {
            try
            {
                var service = new PortQueryService();
                var result = service.Query(port, PortProtocolFilter.All);
                Check(result.IsComplete, "live native API query complete: " + string.Join(",", result.Errors));
                Check(result.Endpoints.Count(endpoint => endpoint.ProcessId == fixture.Id) == 4, "four live endpoints map to fixture PID");
                Check(service.Query(port, PortProtocolFilter.Tcp).Endpoints.Count(endpoint => endpoint.ProcessId == fixture.Id) == 2, "live TCP protocol filter");
                Check(service.Query(port, PortProtocolFilter.Udp).Endpoints.Count(endpoint => endpoint.ProcessId == fixture.Id) == 2, "live UDP protocol filter");
                int anotherPort;
                using (var another = StartFixture(0, out anotherPort))
                {
                    try
                    {
                        var everyPort = service.Query(null, PortProtocolFilter.All);
                        Check(everyPort.IsComplete && everyPort.Endpoints.Count(endpoint => endpoint.ProcessId == fixture.Id) == 4
                            && everyPort.Endpoints.Count(endpoint => endpoint.ProcessId == another.Id) == 4, "live all-port query sees multiple processes and ports");
                        Check(service.Query(null, PortProtocolFilter.Tcp).Endpoints.Count(endpoint => endpoint.ProcessId == another.Id) == 2,
                            "live all-port TCP filter");
                        Check(service.Query(null, PortProtocolFilter.Udp).Endpoints.Count(endpoint => endpoint.ProcessId == another.Id) == 2,
                            "live all-port UDP filter");
                    }
                    finally { StopFixture(another); }
                }
                int sharedPort;
                using (var shared = StartFixture(port, out sharedPort, true))
                {
                    try
                    {
                        var sharedQuery = service.Query(port, PortProtocolFilter.Udp);
                        Check(sharedQuery.IsComplete && sharedQuery.Endpoints.Select(endpoint => endpoint.ProcessId).Distinct().Count() == 2,
                            "same port can show multiple owner PIDs");
                        Check(sharedQuery.Endpoints.Count(endpoint => endpoint.LocalAddress == "127.0.0.1") == 2,
                            "same address and port preserve separate owners");
                    }
                    finally { StopFixture(shared); }
                }
                var target = Target(fixture);
                var changedTime = Target(fixture); changedTime.StartTimeUtcTicks++;
                Check(ProcessTerminationService.Terminate(new[] { changedTime }, Process.GetCurrentProcess().Id, CancellationToken.None).Skipped == 1 && !fixture.HasExited, "changed start time is skipped");
                var changedName = Target(fixture); changedName.NormalizedName = "different-process";
                Check(ProcessTerminationService.Terminate(new[] { changedName }, Process.GetCurrentProcess().Id, CancellationToken.None).Skipped == 1 && !fixture.HasExited, "changed name is skipped");
                var unknown = Target(fixture); unknown.StartTimeUtcTicks = null;
                Check(ProcessTerminationService.Terminate(new[] { unknown }, Process.GetCurrentProcess().Id, CancellationToken.None).Skipped == 1 && !fixture.HasExited, "unknown identity is skipped");
                Check(ProcessTerminationService.Terminate(new[] { target }, Process.GetCurrentProcess().Id, new CancellationToken(true)).Skipped == 1 && !fixture.HasExited, "cancelled operation cannot kill");
                Check(ProcessTerminationService.Terminate(new[] { target }, Process.GetCurrentProcess().Id, CancellationToken.None, t => "not owner").Skipped == 1 && !fixture.HasExited, "ownership changed is skipped");
                var denied = ProcessTerminationService.Terminate(new[] { target }, Process.GetCurrentProcess().Id, CancellationToken.None, t => { throw new Win32Exception(5); });
                Check(denied.Failures.Count == 1 && denied.Failures[0].Contains("权限不足") && !fixture.HasExited, "permission error is reported");
                var current = Process.GetCurrentProcess();
                Check(ProcessTerminationService.Terminate(new[] { Target(current), new ProcessTarget { Id = 0 }, new ProcessTarget { Id = 4 } }, current.Id, CancellationToken.None).Skipped == 3, "self and system protection");
                var ended = ProcessTerminationService.Terminate(new[] { target, target }, current.Id, CancellationToken.None,
                    t => service.Query(port, PortProtocolFilter.All).Endpoints.Any(endpoint => endpoint.ProcessId == t.Id) ? null : "not owner");
                Check(ended.Requested == 1 && ended.Success == 1 && ended.Failures.Count == 0, "deduplicate and terminate fixture");
                Check(service.Query(port, PortProtocolFilter.All).Endpoints.All(endpoint => endpoint.ProcessId != target.Id), "requery confirms fixture endpoints disappeared");
                Check(ProcessTerminationService.Terminate(new[] { target }, current.Id, CancellationToken.None).Skipped == 1, "already exited target is skipped");
                int replacementPort;
                using (var replacement = StartFixture(port, out replacementPort))
                {
                    try
                    {
                        Check(replacementPort == port && service.Query(port, PortProtocolFilter.All).Endpoints.Any(endpoint => endpoint.ProcessId == replacement.Id), "port takeover shows replacement PID");
                        Check(ProcessTerminationService.Terminate(new[] { target }, current.Id, CancellationToken.None).Skipped == 1 && !replacement.HasExited, "stale target does not terminate replacement");
                    }
                    finally { StopFixture(replacement); }
                }
            }
            finally { StopFixture(fixture); }
        }
        Console.WriteLine("PASS: live TCP/UDP IPv4/IPv6 ownership, identity guards, termination and takeover");
    }

    private static Process StartFixture(int requestedPort, out int port, bool udpOnly = false)
    {
        var process = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, (udpOnly ? "--udp-listener " : "--listener ") + requestedPort)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        var ready = process.StandardOutput.ReadLineAsync();
        if (!ready.Wait(10000) || !int.TryParse(ready.Result, out port))
        {
            StopFixture(process);
            throw new Exception("Listener fixture did not start.");
        }
        return process;
    }

    private static void StopFixture(Process process)
    {
        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } }
        catch (InvalidOperationException) { }
    }

    private static int RunListener(string[] args)
    {
        var requestedPort = args.Length > 1 ? int.Parse(args[1]) : 0;
        if (args[0] == "--udp-listener")
        {
            using (var shared = CreateUdpSocket(IPAddress.Loopback, requestedPort))
            {
                Console.WriteLine(((IPEndPoint)shared.Client.LocalEndPoint).Port);
                Console.Out.Flush();
                Thread.Sleep(Timeout.Infinite);
            }
            return 0;
        }
        var tcp4 = new TcpListener(IPAddress.Loopback, requestedPort);
        tcp4.Start();
        var port = ((IPEndPoint)tcp4.LocalEndpoint).Port;
        var tcp6 = new TcpListener(IPAddress.IPv6Loopback, port);
        tcp6.Server.DualMode = false;
        tcp6.Start();
        using (var udp4 = CreateUdpSocket(IPAddress.Loopback, port))
        using (var udp6 = CreateUdpSocket(IPAddress.IPv6Loopback, port))
        {
            Console.WriteLine(port);
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
        }
        return 0;
    }

    private static UdpClient CreateUdpSocket(IPAddress address, int port)
    {
        var socket = new UdpClient(address.AddressFamily);
        socket.Client.ExclusiveAddressUse = false;
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(address, port));
        return socket;
    }
}
