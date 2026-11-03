using System.Reflection;
using System.Runtime.InteropServices;

namespace OutsideRecorderTests;

public sealed class CpuLimitLayoutTests
{
    [Fact]
    public void NativeJobLayoutsUsePointerSizedFieldsAndDocumentedOffsets()
    {
        Type Nested(string name) => typeof(CpuLimit).GetNestedType(name, BindingFlags.NonPublic)!;
        var basic = Nested("BasicLimits"); var io = Nested("IoCounters"); var extended = Nested("ExtendedLimits");
        Assert.Equal(IntPtr.Size == 8 ? 64 : 48, Marshal.SizeOf(basic));
        Assert.Equal(48, Marshal.SizeOf(io));
        Assert.Equal(IntPtr.Size == 8 ? 144 : 112, Marshal.SizeOf(extended));
        Assert.Equal(16, Marshal.OffsetOf(basic, "LimitFlags").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 24 : 20, Marshal.OffsetOf(basic, "MinimumWorkingSetSize").ToInt32());
        Assert.Equal(Marshal.SizeOf(basic), Marshal.OffsetOf(extended, "Io").ToInt32());
        Assert.Equal(Marshal.SizeOf(basic) + 48, Marshal.OffsetOf(extended, "ProcessMemoryLimit").ToInt32());
    }

    [Fact]
    public void HostOnlyJobPermitsAutomaticChildBreakawayWithoutOtherProcessLimits()
    {
        var factory = typeof(CpuLimit).GetMethod("HostOnlyLimits", BindingFlags.Static | BindingFlags.NonPublic)!;
        var limits = factory.Invoke(null, null)!;
        var basic = limits.GetType().GetField("Basic")!.GetValue(limits)!;
        Assert.Equal(0x00001000u, (uint)basic.GetType().GetField("LimitFlags")!.GetValue(basic)!);
        Assert.Equal(0u, (uint)basic.GetType().GetField("ActiveProcessLimit")!.GetValue(basic)!);
    }
}
