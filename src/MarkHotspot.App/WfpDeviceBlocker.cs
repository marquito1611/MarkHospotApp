using System.ComponentModel;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MarkHotspot;

internal static class WfpDeviceBlocker
{
    private static readonly Guid InboundIpv4Layer = new("C86FD1BF-21CD-497E-A0BB-17425C885C58");
    private static readonly Guid ForwardIpv4Layer = new("A82ACC24-4EE1-4EE1-B465-FD1D25CB10A4");
    private static readonly Guid ForwardIpv6Layer = new("7B964818-19C7-493A-BE3B-7A4D59D1C32C");

    private static readonly Guid RemoteAddressCondition = new("B235AE9A-1D64-49B8-A44C-5FF3D9095045");
    private static readonly Guid InterfaceIndexCondition = new("667FD755-D695-434A-8AF5-D3835A1259BC");
    private static readonly Guid ForwardSourceAddressCondition = new("AE96897E-2E94-4BC9-B313-B27EE80E574D");
    private static readonly Guid ForwardDestinationAddressCondition = new("2D79133B-B390-45C6-8699-ACACEAAFED33");
    private static readonly Guid SourceInterfaceIndexCondition = new("2311334D-C92D-45BF-9496-EDF447820E2D");
    private static readonly Guid DestinationInterfaceIndexCondition = new("35CF6522-4139-45EE-A0D5-67B80949D879");
    private static readonly Guid UniversalSublayer = new("EEBECC03-CED4-4380-819A-2734397B2B74");

    public static void Block(string ipAddress, uint hotspotInterfaceIndex, IEnumerable<string>? ipv6Addresses = null)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Se necesita una dirección IPv4 válida.", nameof(ipAddress));

        var installed = new List<Guid>();
        var engineStatus = FwpmEngineOpen0(IntPtr.Zero, 10, IntPtr.Zero, IntPtr.Zero, out var engine);
        ThrowIfFailed(engineStatus, "No se pudo abrir Windows Filtering Platform");

        try
        {
            // Client-originated packets entering the host through the hotspot interface.
            AddBlockFilter(engine, ip, InboundIpv4Layer, "inbound", RemoteAddressCondition,
                InterfaceIndexCondition, hotspotInterfaceIndex);
            installed.Add(CreateFilterKey(ip.ToString(), "inbound"));

            // Forwarded upload packets: the client is the source and its traffic enters
            // through the hotspot interface. IPFORWARD_V4 has layer-specific conditions;
            // using IP_REMOTE_ADDRESS / INTERFACE_INDEX here makes FwpmFilterAdd fail.
            AddBlockFilter(engine, ip, ForwardIpv4Layer, "forward-upload", ForwardSourceAddressCondition,
                SourceInterfaceIndexCondition, hotspotInterfaceIndex);
            installed.Add(CreateFilterKey(ip.ToString(), "forward-upload"));

            // Forwarded download packets: the client is the destination and its traffic
            // leaves through the hotspot interface.
            AddBlockFilter(engine, ip, ForwardIpv4Layer, "forward-download", ForwardDestinationAddressCondition,
                DestinationInterfaceIndexCondition, hotspotInterfaceIndex);
            installed.Add(CreateFilterKey(ip.ToString(), "forward-download"));

            // IPv6 must be filtered separately: IPv4-only rules do not stop a client
            // that has an IPv6 address and routes Internet traffic over IPv6.
            foreach (var addressText in (ipv6Addresses ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!IPAddress.TryParse(addressText, out var ipv6) ||
                    ipv6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
                    ipv6.IsIPv6Multicast || ipv6.Equals(IPAddress.IPv6Any) || ipv6.Equals(IPAddress.IPv6Loopback))
                    continue;

                AddBlockFilterV6(engine, ipv6, "forward-upload", ForwardSourceAddressCondition,
                    SourceInterfaceIndexCondition, hotspotInterfaceIndex);
                installed.Add(CreateFilterKey(ipv6.ToString(), "v6-forward-upload"));

                AddBlockFilterV6(engine, ipv6, "forward-download", ForwardDestinationAddressCondition,
                    DestinationInterfaceIndexCondition, hotspotInterfaceIndex);
                installed.Add(CreateFilterKey(ipv6.ToString(), "v6-forward-download"));
            }
        }
        catch
        {
            foreach (var key in installed)
            {
                try { DeleteIfPresent(engine, key); } catch { }
            }
            throw;
        }
        finally { FwpmEngineClose0(engine); }
    }

    public static void Unblock(string ipAddress, IEnumerable<string>? ipv6Addresses = null)
    {
        var status = FwpmEngineOpen0(IntPtr.Zero, 10, IntPtr.Zero, IntPtr.Zero, out var engine);
        ThrowIfFailed(status, "No se pudo abrir Windows Filtering Platform");
        try
        {
            DeleteIfPresent(engine, CreateFilterKey(ipAddress, "inbound"));
            DeleteIfPresent(engine, CreateFilterKey(ipAddress, "forward-upload"));
            DeleteIfPresent(engine, CreateFilterKey(ipAddress, "forward-download"));
            // Remove filters created by earlier builds.
            DeleteIfPresent(engine, CreateFilterKey(ipAddress, "forward"));
            DeleteIfPresent(engine, CreateLegacyFilterKey(ipAddress));
            foreach (var address in (ipv6Addresses ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                DeleteIfPresent(engine, CreateFilterKey(address, "v6-forward-upload"));
                DeleteIfPresent(engine, CreateFilterKey(address, "v6-forward-download"));
            }
        }
        finally { FwpmEngineClose0(engine); }
    }

    private static void AddBlockFilter(
        IntPtr engine, IPAddress ip, Guid layer, string direction,
        Guid addressCondition, Guid interfaceCondition, uint interfaceIndex)
    {
        var key = CreateFilterKey(ip.ToString(), direction);
        DeleteIfPresent(engine, key);

        var ipBytes = ip.GetAddressBytes();
        // FWP_V4_ADDR_AND_MASK uses host-order numeric IPv4 values. Reading the
        // address bytes as little-endian reverses the address (e.g. 192.168.137.30)
        // and installs a filter that never matches the intended client.
        var addressMask = new FwpV4AddressAndMask
        {
            Address = BinaryPrimitives.ReadUInt32BigEndian(ipBytes),
            Mask = uint.MaxValue
        };
        var maskPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FwpV4AddressAndMask>());
        var conditionsPointer = IntPtr.Zero;
        var name = Marshal.StringToHGlobalUni("MarkHotspot bloquea " + ip + " (" + direction + ")");
        var description = Marshal.StringToHGlobalUni("Filtro persistente de MarkHotspot para bloquear el tráfico IPv4 del cliente conectado al hotspot.");
        try
        {
            Marshal.StructureToPtr(addressMask, maskPointer, false);
            var conditions = new[]
            {
                new FwpmFilterCondition
                {
                    FieldKey = addressCondition,
                    MatchType = 0, // FWP_MATCH_EQUAL
                    ConditionValue = new FwpConditionValue { Type = 0x100, V4AddressMask = maskPointer } // FWP_V4_ADDR_MASK = FWP_SINGLE_DATA_TYPE_MAX + 1 (0x100)
                },
                new FwpmFilterCondition
                {
                    FieldKey = interfaceCondition,
                    MatchType = 0,
                    ConditionValue = new FwpConditionValue { Type = 3, UInt32 = interfaceIndex } // FWP_UINT32
                }
            };

            var conditionSize = Marshal.SizeOf<FwpmFilterCondition>();
            conditionsPointer = Marshal.AllocHGlobal(conditionSize * conditions.Length);
            for (var i = 0; i < conditions.Length; i++)
                Marshal.StructureToPtr(conditions[i], IntPtr.Add(conditionsPointer, i * conditionSize), false);

            var filter = new FwpmFilter
            {
                FilterKey = key,
                DisplayData = new FwpmDisplayData { Name = name, Description = description },
                Flags = 1, // FWPM_FILTER_FLAG_PERSISTENT
                LayerKey = layer,
                SubLayerKey = UniversalSublayer,
                Weight = new FwpValue { Type = 0 },
                NumberOfFilterConditions = (uint)conditions.Length,
                FilterConditions = conditionsPointer,
                Action = new FwpmAction { Type = 0x1001 } // FWP_ACTION_BLOCK
            };
            var addStatus = FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _);
            ThrowIfFailed(addStatus, "Windows no pudo instalar el filtro de bloqueo " + direction);
        }
        finally
        {
            if (conditionsPointer != IntPtr.Zero) Marshal.FreeHGlobal(conditionsPointer);
            Marshal.FreeHGlobal(maskPointer);
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(description);
        }
    }

    private static void AddBlockFilterV6(
        IntPtr engine, IPAddress ip, string direction,
        Guid addressCondition, Guid interfaceCondition, uint interfaceIndex)
    {
        var key = CreateFilterKey(ip.ToString(), "v6-" + direction);
        DeleteIfPresent(engine, key);

        var addressMask = new FwpV6AddressAndMask
        {
            Address = ip.GetAddressBytes(),
            PrefixLength = 128
        };
        var maskPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FwpV6AddressAndMask>());
        var conditionsPointer = IntPtr.Zero;
        var name = Marshal.StringToHGlobalUni("MarkHotspot bloquea IPv6 " + ip + " (" + direction + ")");
        var description = Marshal.StringToHGlobalUni("Filtro WFP de reenvío IPv6 para bloquear el tráfico del cliente del hotspot.");
        try
        {
            Marshal.StructureToPtr(addressMask, maskPointer, false);
            var conditions = new[]
            {
                new FwpmFilterCondition
                {
                    FieldKey = addressCondition,
                    MatchType = 0,
                    ConditionValue = new FwpConditionValue { Type = 0x101, V6AddressMask = maskPointer }
                },
                new FwpmFilterCondition
                {
                    FieldKey = interfaceCondition,
                    MatchType = 0,
                    ConditionValue = new FwpConditionValue { Type = 3, UInt32 = interfaceIndex }
                }
            };

            var conditionSize = Marshal.SizeOf<FwpmFilterCondition>();
            conditionsPointer = Marshal.AllocHGlobal(conditionSize * conditions.Length);
            for (var i = 0; i < conditions.Length; i++)
                Marshal.StructureToPtr(conditions[i], IntPtr.Add(conditionsPointer, i * conditionSize), false);

            var filter = new FwpmFilter
            {
                FilterKey = key,
                DisplayData = new FwpmDisplayData { Name = name, Description = description },
                Flags = 1,
                LayerKey = ForwardIpv6Layer,
                SubLayerKey = UniversalSublayer,
                Weight = new FwpValue { Type = 0 },
                NumberOfFilterConditions = (uint)conditions.Length,
                FilterConditions = conditionsPointer,
                Action = new FwpmAction { Type = 0x1001 }
            };
            var addStatus = FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _);
            ThrowIfFailed(addStatus, "Windows no pudo instalar el filtro de bloqueo IPv6 " + direction);
        }
        finally
        {
            if (conditionsPointer != IntPtr.Zero) Marshal.FreeHGlobal(conditionsPointer);
            Marshal.FreeHGlobal(maskPointer);
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(description);
        }
    }

    private static Guid CreateFilterKey(string ipAddress, string direction)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("MarkHotspot-WFP-Block-" + direction + "-" + ipAddress));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Guid CreateLegacyFilterKey(string ipAddress)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("MarkHotspot-WFP-Block-" + ipAddress));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void DeleteIfPresent(IntPtr engine, Guid key)
    {
        var status = FwpmFilterDeleteByKey0(engine, ref key);
        if (status != 0 && status != unchecked((uint)0x80320003))
            ThrowIfFailed(status, "No se pudo quitar el filtro anterior");
    }

    private static void ThrowIfFailed(uint status, string message)
    {
        if (status != 0)
            throw new Win32Exception(unchecked((int)status), message + $" (código 0x{status:X8}).");
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmEngineOpen0(IntPtr serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);
    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineClose0(IntPtr engineHandle);
    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FwpmFilter filter, IntPtr securityDescriptor, out ulong id);
    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmDisplayData { public IntPtr Name; public IntPtr Description; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FwpByteBlob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FwpValue
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public byte UInt8;
        [FieldOffset(8)] public IntPtr UInt64;
    }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FwpConditionValue
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public IntPtr V4AddressMask;
        [FieldOffset(8)] public IntPtr V6AddressMask;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV4AddressAndMask { public uint Address; public uint Mask; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV6AddressAndMask
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Address;
        public byte PrefixLength;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilterCondition
    {
        public Guid FieldKey;
        public int MatchType;
        public FwpConditionValue ConditionValue;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmAction { public int Type; public Guid CalloutKey; }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FwpmFilterContext
    {
        [FieldOffset(0)] public ulong RawContext;
        [FieldOffset(0)] public Guid ProviderContextKey;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilter
    {
        public Guid FilterKey;
        public FwpmDisplayData DisplayData;
        public uint Flags;
        public IntPtr ProviderKey;
        public FwpByteBlob ProviderData;
        public Guid LayerKey;
        public Guid SubLayerKey;
        public FwpValue Weight;
        public uint NumberOfFilterConditions;
        public IntPtr FilterConditions;
        public FwpmAction Action;
        public FwpmFilterContext Context;
        public IntPtr Reserved;
        public ulong FilterId;
        public FwpValue EffectiveWeight;
    }
}
