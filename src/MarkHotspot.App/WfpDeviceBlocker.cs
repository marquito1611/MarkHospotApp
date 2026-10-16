using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MarkHotspot;

internal static class WfpDeviceBlocker
{
    private static readonly Guid InboundIpv4Layer = new("C86FD1BF-21CD-497E-A0BB-17425C885C58");
    private static readonly Guid ForwardIpv4Layer = new("A82ACC24-4EE1-4EE1-B465-FD1D25CB10A4");
    private static readonly Guid RemoteAddressCondition = new("B235AE9A-1D64-49B8-A44C-5FF3D9095045");
    private static readonly Guid InterfaceIndexCondition = new("667FD755-D695-434A-8AF5-D3835A1259BC");
    private static readonly Guid UniversalSublayer = new("EEBECC03-CED4-4380-819A-2734397B2B74");

    public static void Block(string ipAddress, uint interfaceIndex)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Se necesita una dirección IPv4 válida.", nameof(ipAddress));

        var status = FwpmEngineOpen0(IntPtr.Zero, 10, IntPtr.Zero, IntPtr.Zero, out var engine);
        ThrowIfFailed(status, "No se pudo abrir Windows Filtering Platform");
        try
        {
            AddBlockFilter(engine, ip, interfaceIndex, InboundIpv4Layer, "inbound");
            try
            {
                // Also filter the forwarding path so return/download packets for this client are discarded.
                AddBlockFilter(engine, ip, interfaceIndex, ForwardIpv4Layer, "forward");
            }
            catch
            {
                DeleteIfPresent(engine, CreateFilterKey(ipAddress, "inbound"));
                throw;
            }
        }
        finally { FwpmEngineClose0(engine); }
    }

    public static void Unblock(string ipAddress)
    {
        var status = FwpmEngineOpen0(IntPtr.Zero, 10, IntPtr.Zero, IntPtr.Zero, out var engine);
        ThrowIfFailed(status, "No se pudo abrir Windows Filtering Platform");
        try
        {
            DeleteIfPresent(engine, CreateFilterKey(ipAddress, "inbound"));
            DeleteIfPresent(engine, CreateFilterKey(ipAddress, "forward"));
            DeleteIfPresent(engine, CreateLegacyFilterKey(ipAddress));
            // Remove the single inbound filter used by pre-cache versions of MarkHotspot.
            DeleteIfPresent(engine, CreateLegacyFilterKey(ipAddress));
        }
        finally { FwpmEngineClose0(engine); }
    }

    private static void AddBlockFilter(IntPtr engine, IPAddress ip, uint interfaceIndex, Guid layer, string direction)
    {
        var key = CreateFilterKey(ip.ToString(), direction);
        DeleteIfPresent(engine, key);

        var ipBytes = ip.GetAddressBytes();
        var addressMask = new FwpV4AddressAndMask { Address = BitConverter.ToUInt32(ipBytes, 0), Mask = uint.MaxValue };
        var maskPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FwpV4AddressAndMask>());
        var conditionsPointer = IntPtr.Zero;
        var name = Marshal.StringToHGlobalUni("MarkHotspot bloquea " + ip + " (" + direction + ")");
        var description = Marshal.StringToHGlobalUni("Filtro persistente de MarkHotspot para el tráfico IPv4 del cliente en la interfaz del hotspot.");
        try
        {
            Marshal.StructureToPtr(addressMask, maskPointer, false);
            var conditions = new[]
            {
                new FwpmFilterCondition
                {
                    FieldKey = RemoteAddressCondition,
                    MatchType = 0,
                    ConditionValue = new FwpConditionValue { Type = 13, V4AddressMask = maskPointer }
                },
                new FwpmFilterCondition
                {
                    FieldKey = InterfaceIndexCondition,
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
                Flags = 1, // FWPM_FILTER_FLAG_PERSISTENT
                LayerKey = layer,
                SubLayerKey = UniversalSublayer,
                Weight = new FwpValue { Type = 0 },
                NumberOfFilterConditions = (uint)conditions.Length,
                FilterConditions = conditionsPointer,
                Action = new FwpmAction { Type = 0x1001 }
            };
            var status = FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _);
            ThrowIfFailed(status, "Windows no pudo instalar el filtro de bloqueo " + direction);
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
        if (status != 0) throw new Win32Exception(unchecked((int)status), message + $" (código 0x{status:X8}).");
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
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV4AddressAndMask { public uint Address; public uint Mask; }
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
