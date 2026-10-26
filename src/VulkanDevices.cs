using System.Runtime.InteropServices;

namespace RecordSummary;

/// <summary>
/// Список видеокарт в той же нумерации, что у ggml (whisper.cpp и llama.cpp): порядок Vulkan,
/// только встроенные и дискретные GPU, дубликаты одного GPU от разных драйверов отбрасываются.
/// </summary>
public static class VulkanDevices
{
    public sealed record Device(int Index, string Name, bool Integrated)
    {
        public override string ToString() => $"Vulkan{Index}: {Name} ({(Integrated ? "встроенная" : "дискретная")})";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VkInstanceCreateInfo
    {
        public int sType;
        public IntPtr pNext;
        public uint flags;
        public IntPtr pApplicationInfo;
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    [DllImport("vulkan-1.dll")] static extern int vkCreateInstance(ref VkInstanceCreateInfo info, IntPtr allocator, out IntPtr instance);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);
    [DllImport("vulkan-1.dll")] static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr[]? devices);
    [DllImport("vulkan-1.dll")] static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

    const int VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU = 1, VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU = 2;

    static IReadOnlyList<Device>? _cache;

    public static IReadOnlyList<Device> List() => _cache ??= Enumerate();

    static IReadOnlyList<Device> Enumerate()
    {
        try
        {
            var info = new VkInstanceCreateInfo { sType = 1 }; // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
            if (vkCreateInstance(ref info, IntPtr.Zero, out var instance) != 0) return [];
            try
            {
                uint count = 0;
                vkEnumeratePhysicalDevices(instance, ref count, null);
                var handles = new IntPtr[count];
                vkEnumeratePhysicalDevices(instance, ref count, handles);

                var result = new List<Device>();
                var seen = new HashSet<(int, int)>();
                // VkPhysicalDeviceProperties: apiVersion, driverVersion, vendorID@8, deviceID@12, deviceType@16, deviceName[256]@20
                var props = Marshal.AllocHGlobal(1024);
                try
                {
                    foreach (var h in handles)
                    {
                        vkGetPhysicalDeviceProperties(h, props);
                        int vendor = Marshal.ReadInt32(props, 8), id = Marshal.ReadInt32(props, 12), type = Marshal.ReadInt32(props, 16);
                        if (type is not (VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU or VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU)) continue;
                        if (!seen.Add((vendor, id))) continue;
                        var name = Marshal.PtrToStringUTF8(props + 20) ?? "?";
                        result.Add(new Device(result.Count, name, type == VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU));
                    }
                }
                finally { Marshal.FreeHGlobal(props); }
                return result;
            }
            finally { vkDestroyInstance(instance, IntPtr.Zero); }
        }
        catch (Exception e)
        {
            Log.Write($"Vulkan недоступен: {e.Message}");
            return [];
        }
    }
}
