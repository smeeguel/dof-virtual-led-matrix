using System.Runtime.InteropServices;
using System.Text;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

internal static class VulkanCapabilityProbe
{
    private const uint VkQueueGraphicsBit = 0x00000001;
    private const int VkSuccess = 0;

    public static bool TryProbe(out string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            reason = "Vulkan probe failed: host OS is not Windows.";
            return false;
        }

        var library = LoadLibrary("vulkan-1.dll");
        if (library == IntPtr.Zero)
        {
            reason = "Vulkan probe failed: vulkan-1.dll (loader) was not found.";
            return false;
        }

        try
        {
            var getInstanceProcAddrPtr = GetProcAddress(library, "vkGetInstanceProcAddr");
            if (getInstanceProcAddrPtr == IntPtr.Zero)
            {
                reason = "Vulkan probe failed: vkGetInstanceProcAddr export is missing.";
                return false;
            }

            var getInstanceProcAddr = Marshal.GetDelegateForFunctionPointer<VkGetInstanceProcAddrDelegate>(getInstanceProcAddrPtr);

            var createInstance = LoadGlobalProc<VkCreateInstanceDelegate>(getInstanceProcAddr, "vkCreateInstance");
            if (createInstance is null)
            {
                reason = "Vulkan probe failed: vkCreateInstance is unavailable from loader.";
                return false;
            }

            var instanceExtensions = new[]
            {
                "VK_KHR_surface",
                "VK_KHR_win32_surface",
            };

            var instance = IntPtr.Zero;
            IntPtr[]? extensionNamePointers = null;
            IntPtr appNamePtr = IntPtr.Zero;
            IntPtr engineNamePtr = IntPtr.Zero;
            IntPtr extensionsPtr = IntPtr.Zero;

            try
            {
                extensionNamePointers = AllocateUtf8Strings(instanceExtensions);
                appNamePtr = StringToHGlobalUtf8("VirtualDofMatrix.Probe");
                engineNamePtr = StringToHGlobalUtf8("VirtualDofMatrix");
                extensionsPtr = Marshal.AllocHGlobal(IntPtr.Size * extensionNamePointers.Length);
                for (var i = 0; i < extensionNamePointers.Length; i++)
                {
                    Marshal.WriteIntPtr(extensionsPtr, i * IntPtr.Size, extensionNamePointers[i]);
                }

                var appInfo = new VkApplicationInfo
                {
                    sType = VkStructureType.ApplicationInfo,
                    pApplicationName = appNamePtr,
                    applicationVersion = 1,
                    pEngineName = engineNamePtr,
                    engineVersion = 1,
                    apiVersion = VkMakeApiVersion(0, 1, 0, 0),
                };

                var instanceInfo = new VkInstanceCreateInfo
                {
                    sType = VkStructureType.InstanceCreateInfo,
                    pApplicationInfo = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>()),
                    enabledExtensionCount = (uint)extensionNamePointers.Length,
                    ppEnabledExtensionNames = extensionsPtr,
                };
                Marshal.StructureToPtr(appInfo, instanceInfo.pApplicationInfo, false);

                var createResult = createInstance(ref instanceInfo, IntPtr.Zero, out instance);
                Marshal.FreeHGlobal(instanceInfo.pApplicationInfo);
                if (createResult != VkSuccess || instance == IntPtr.Zero)
                {
                    reason = $"Vulkan probe failed: vkCreateInstance returned {createResult}.";
                    return false;
                }
            }
            finally
            {
                if (extensionsPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(extensionsPtr);
                }

                if (extensionNamePointers is not null)
                {
                    foreach (var ptr in extensionNamePointers)
                    {
                        if (ptr != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(ptr);
                        }
                    }
                }

                if (appNamePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(appNamePtr);
                }

                if (engineNamePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(engineNamePtr);
                }
            }

            try
            {
                var enumeratePhysicalDevices = LoadInstanceProc<VkEnumeratePhysicalDevicesDelegate>(getInstanceProcAddr, instance, "vkEnumeratePhysicalDevices");
                if (enumeratePhysicalDevices is null)
                {
                    reason = "Vulkan probe failed: vkEnumeratePhysicalDevices not available.";
                    return false;
                }

                uint physicalDeviceCount = 0;
                var enumerateResult = enumeratePhysicalDevices(instance, ref physicalDeviceCount, IntPtr.Zero);
                if (enumerateResult != VkSuccess || physicalDeviceCount == 0)
                {
                    reason = enumerateResult != VkSuccess
                        ? $"Vulkan probe failed: vkEnumeratePhysicalDevices returned {enumerateResult}."
                        : "Vulkan probe failed: no physical devices were reported.";
                    return false;
                }

                var devicesBuffer = Marshal.AllocHGlobal(IntPtr.Size * (int)physicalDeviceCount);
                try
                {
                    enumerateResult = enumeratePhysicalDevices(instance, ref physicalDeviceCount, devicesBuffer);
                    if (enumerateResult != VkSuccess)
                    {
                        reason = $"Vulkan probe failed: vkEnumeratePhysicalDevices(device list) returned {enumerateResult}.";
                        return false;
                    }

                    var getQueueFamilies = LoadInstanceProc<VkGetPhysicalDeviceQueueFamilyPropertiesDelegate>(
                        getInstanceProcAddr,
                        instance,
                        "vkGetPhysicalDeviceQueueFamilyProperties");
                    var getWin32PresentSupport = LoadInstanceProc<VkGetPhysicalDeviceWin32PresentationSupportKHRDelegate>(
                        getInstanceProcAddr,
                        instance,
                        "vkGetPhysicalDeviceWin32PresentationSupportKHR");

                    if (getQueueFamilies is null)
                    {
                        reason = "Vulkan probe failed: vkGetPhysicalDeviceQueueFamilyProperties not available.";
                        return false;
                    }

                    if (getWin32PresentSupport is null)
                    {
                        reason = "Vulkan probe failed: vkGetPhysicalDeviceWin32PresentationSupportKHR not available.";
                        return false;
                    }

                    var hasGraphicsQueue = false;
                    var hasGraphicsAndPresent = false;

                    for (var deviceIndex = 0; deviceIndex < physicalDeviceCount; deviceIndex++)
                    {
                        var physicalDevice = Marshal.ReadIntPtr(devicesBuffer, deviceIndex * IntPtr.Size);
                        uint queueFamilyCount = 0;
                        getQueueFamilies(physicalDevice, ref queueFamilyCount, IntPtr.Zero);
                        if (queueFamilyCount == 0)
                        {
                            continue;
                        }

                        var queuePropertiesPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkQueueFamilyProperties>() * (int)queueFamilyCount);
                        try
                        {
                            getQueueFamilies(physicalDevice, ref queueFamilyCount, queuePropertiesPtr);

                            for (uint queueFamilyIndex = 0; queueFamilyIndex < queueFamilyCount; queueFamilyIndex++)
                            {
                                var propertyPtr = queuePropertiesPtr + (int)(queueFamilyIndex * (uint)Marshal.SizeOf<VkQueueFamilyProperties>());
                                var queueProperties = Marshal.PtrToStructure<VkQueueFamilyProperties>(propertyPtr);
                                if ((queueProperties.queueFlags & VkQueueGraphicsBit) == 0)
                                {
                                    continue;
                                }

                                hasGraphicsQueue = true;
                                var supportsPresent = getWin32PresentSupport(physicalDevice, queueFamilyIndex);
                                if (supportsPresent != 0)
                                {
                                    hasGraphicsAndPresent = true;
                                    break;
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(queuePropertiesPtr);
                        }

                        if (hasGraphicsAndPresent)
                        {
                            break;
                        }
                    }

                    if (!hasGraphicsQueue)
                    {
                        reason = "Vulkan probe failed: no graphics-capable queue family found.";
                        return false;
                    }

                    if (!hasGraphicsAndPresent)
                    {
                        reason = "Vulkan probe failed: no queue family supports both graphics and Win32 presentation.";
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(devicesBuffer);
                }
            }
            finally
            {
                if (instance != IntPtr.Zero)
                {
                    var destroyInstance = LoadInstanceProc<VkDestroyInstanceDelegate>(getInstanceProcAddr, instance, "vkDestroyInstance");
                    destroyInstance?.Invoke(instance, IntPtr.Zero);
                }
            }
        }
        catch (Exception ex)
        {
            reason = $"Vulkan probe failed: unexpected exception: {ex.Message}";
            return false;
        }
        finally
        {
            FreeLibrary(library);
        }

        reason = "Vulkan probe succeeded.";
        return true;
    }

    private static TDelegate? LoadGlobalProc<TDelegate>(VkGetInstanceProcAddrDelegate getProc, string name)
        where TDelegate : class
    {
        var namePtr = StringToHGlobalUtf8(name);
        try
        {
            var proc = getProc(IntPtr.Zero, namePtr);
            return proc == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(proc, typeof(TDelegate)) as TDelegate;
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    private static TDelegate? LoadInstanceProc<TDelegate>(VkGetInstanceProcAddrDelegate getProc, IntPtr instance, string name)
        where TDelegate : class
    {
        var namePtr = StringToHGlobalUtf8(name);
        try
        {
            var proc = getProc(instance, namePtr);
            return proc == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(proc, typeof(TDelegate)) as TDelegate;
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    private static IntPtr[] AllocateUtf8Strings(IEnumerable<string> values) => values.Select(StringToHGlobalUtf8).ToArray();

    private static IntPtr StringToHGlobalUtf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + '\0');
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static uint VkMakeApiVersion(uint variant, uint major, uint minor, uint patch)
        => (variant << 29) | (major << 22) | (minor << 12) | patch;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VkGetInstanceProcAddrDelegate(IntPtr instance, IntPtr pName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VkCreateInstanceDelegate(ref VkInstanceCreateInfo pCreateInfo, IntPtr pAllocator, out IntPtr pInstance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VkDestroyInstanceDelegate(IntPtr instance, IntPtr pAllocator);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VkEnumeratePhysicalDevicesDelegate(IntPtr instance, ref uint pPhysicalDeviceCount, IntPtr pPhysicalDevices);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VkGetPhysicalDeviceQueueFamilyPropertiesDelegate(IntPtr physicalDevice, ref uint pQueueFamilyPropertyCount, IntPtr pQueueFamilyProperties);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint VkGetPhysicalDeviceWin32PresentationSupportKHRDelegate(IntPtr physicalDevice, uint queueFamilyIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public VkStructureType sType;
        public IntPtr pNext;
        public IntPtr pApplicationName;
        public uint applicationVersion;
        public IntPtr pEngineName;
        public uint engineVersion;
        public uint apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public VkStructureType sType;
        public IntPtr pNext;
        public uint flags;
        public IntPtr pApplicationInfo;
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkQueueFamilyProperties
    {
        public uint queueFlags;
        public uint queueCount;
        public uint timestampValidBits;
        public VkExtent3D minImageTransferGranularity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkExtent3D
    {
        public uint width;
        public uint height;
        public uint depth;
    }

    private enum VkStructureType : uint
    {
        ApplicationInfo = 0,
        InstanceCreateInfo = 1,
    }
}
