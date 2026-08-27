using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DS4Windows
{
    internal readonly record struct HidHideDeviceNode(
        Guid ContainerId,
        Guid ClassGuid,
        string ParentInstanceId);

    internal interface IHidHideDeviceNodeTree
    {
        bool TryGetNode(string instanceId, out HidHideDeviceNode node);
        IReadOnlyList<string> GetChildren(string instanceId);
    }

    /// <summary>
    /// Resolves the instance paths selected by HidHide Configuration Client for
    /// one HID collection.  In particular, a mixed USB controller/audio
    /// container is never blacklisted at its base node.
    /// </summary>
    internal static class HidHideDeviceIdentity
    {
        internal static readonly Guid HidClassGuid =
            new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
        internal static readonly Guid XusbClassGuid =
            new Guid("d61ca365-5af4-4486-998b-9db4734c6ca3");
        internal static readonly Guid SystemContainerId =
            new Guid("00000000-0000-0000-ffff-ffffffffffff");

        public static IReadOnlyList<string> Resolve(string hidInstanceId)
        {
            return ExpandToBaseContainerAndChildren(hidInstanceId,
                ConfigurationManagerHidHideDeviceNodeTree.Instance);
        }

        internal static IReadOnlyList<string> ExpandToBaseContainerAndChildren(
            string hidInstanceId, IHidHideDeviceNodeTree tree)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(hidInstanceId) || tree == null)
            {
                return result;
            }

            AddUnique(result, hidInstanceId);
            if (!tree.TryGetNode(hidInstanceId, out HidHideDeviceNode hidNode) ||
                hidNode.ContainerId == Guid.Empty ||
                hidNode.ContainerId == SystemContainerId)
            {
                return result;
            }

            string baseInstanceId = hidInstanceId;
            HidHideDeviceNode baseNode = hidNode;
            HashSet<string> visited = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase) { hidInstanceId };

            for (int depth = 0; depth < 32; depth++)
            {
                string parentId = baseNode.ParentInstanceId;
                if (string.IsNullOrWhiteSpace(parentId) ||
                    !visited.Add(parentId) ||
                    !tree.TryGetNode(parentId, out HidHideDeviceNode parent) ||
                    parent.ContainerId != hidNode.ContainerId)
                {
                    break;
                }

                baseInstanceId = parentId;
                baseNode = parent;
            }

            IReadOnlyList<string> children = tree.GetChildren(baseInstanceId) ??
                Array.Empty<string>();
            List<string> hidChildren = new List<string>();
            int totalChildren = 0;
            bool everyChildIsSameContainerHid = true;
            foreach (string childId in children)
            {
                if (string.IsNullOrWhiteSpace(childId))
                {
                    continue;
                }

                // HidHide Config Client's base-node safety decision counts
                // every immediate child.  An unreadable, foreign-container,
                // or non-HID child must make the base ineligible; silently
                // dropping it could cloak a mixed controller/audio USB base.
                totalChildren++;
                if (!tree.TryGetNode(childId, out HidHideDeviceNode child) ||
                    child.ContainerId != hidNode.ContainerId ||
                    child.ClassGuid != HidClassGuid)
                {
                    everyChildIsSameContainerHid = false;
                    continue;
                }

                hidChildren.Add(childId);
            }

            bool allChildrenAreHid = totalChildren > 0 &&
                everyChildIsSameContainerHid &&
                hidChildren.Count == totalChildren;
            bool safeBaseClass = baseNode.ClassGuid == HidClassGuid ||
                baseNode.ClassGuid == XusbClassGuid;
            if (allChildrenAreHid && safeBaseClass)
            {
                AddUnique(result, baseInstanceId);
            }

            foreach (string childId in hidChildren)
            {
                AddUnique(result, childId);
            }

            return result;
        }

        private static void AddUnique(List<string> result, string instanceId)
        {
            if (!result.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(instanceId);
            }
        }
    }

    internal sealed class ConfigurationManagerHidHideDeviceNodeTree :
        IHidHideDeviceNodeTree
    {
        private const int CrSuccess = 0;
        private const uint LocatePhantom = 0x00000001;
        private const uint DevPropTypeGuid = 0x0000000D;
        private const uint DevPropTypeString = 0x00000012;

        private static readonly DevPropKey ContainerIdKey = new DevPropKey
        {
            FormatId = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
            PropertyId = 2,
        };
        private static readonly DevPropKey ClassGuidKey = new DevPropKey
        {
            FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            PropertyId = 10,
        };
        private static readonly DevPropKey InstanceIdKey = new DevPropKey
        {
            FormatId = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
            PropertyId = 256,
        };

        public static ConfigurationManagerHidHideDeviceNodeTree Instance { get; } =
            new ConfigurationManagerHidHideDeviceNodeTree();

        public bool TryGetNode(string instanceId, out HidHideDeviceNode node)
        {
            node = default;
            if (!TryLocate(instanceId, out uint deviceNode))
            {
                return false;
            }

            Guid containerId = GetGuidProperty(deviceNode, ContainerIdKey);
            Guid classGuid = GetGuidProperty(deviceNode, ClassGuidKey);
            string parentId = null;
            if (CM_Get_Parent(out uint parent, deviceNode, 0) == CrSuccess)
            {
                parentId = GetStringProperty(parent, InstanceIdKey);
            }

            node = new HidHideDeviceNode(containerId, classGuid, parentId);
            return true;
        }

        public IReadOnlyList<string> GetChildren(string instanceId)
        {
            List<string> result = new List<string>();
            if (!TryLocate(instanceId, out uint deviceNode) ||
                CM_Get_Child(out uint child, deviceNode, 0) != CrSuccess)
            {
                return result;
            }

            for (int siblingCount = 0; siblingCount < 256; siblingCount++)
            {
                string childId = GetStringProperty(child, InstanceIdKey);
                if (!string.IsNullOrWhiteSpace(childId))
                {
                    result.Add(childId);
                }

                if (CM_Get_Sibling(out uint sibling, child, 0) != CrSuccess)
                {
                    break;
                }
                child = sibling;
            }

            return result;
        }

        private static bool TryLocate(string instanceId, out uint deviceNode)
        {
            deviceNode = 0;
            return !string.IsNullOrWhiteSpace(instanceId) &&
                CM_Locate_DevNode(out deviceNode, instanceId, LocatePhantom) ==
                    CrSuccess;
        }

        private static Guid GetGuidProperty(uint deviceNode, DevPropKey key)
        {
            byte[] buffer = new byte[16];
            uint size = (uint)buffer.Length;
            int result = CM_Get_DevNode_Property(deviceNode, in key,
                out uint propertyType, buffer, ref size, 0);
            return result == CrSuccess && propertyType == DevPropTypeGuid &&
                size >= 16 ? new Guid(buffer) : Guid.Empty;
        }

        private static string GetStringProperty(uint deviceNode, DevPropKey key)
        {
            uint size = 0;
            _ = CM_Get_DevNode_Property(deviceNode, in key, out _, null,
                ref size, 0);
            if (size == 0)
            {
                return null;
            }

            byte[] buffer = new byte[size];
            int result = CM_Get_DevNode_Property(deviceNode, in key,
                out uint propertyType, buffer, ref size, 0);
            if (result != CrSuccess || propertyType != DevPropTypeString)
            {
                return null;
            }

            return Encoding.Unicode.GetString(buffer, 0, checked((int)size))
                .TrimEnd('\0');
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct DevPropKey
        {
            public Guid FormatId { get; init; }
            public uint PropertyId { get; init; }
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode,
            EntryPoint = "CM_Locate_DevNodeW")]
        private static extern int CM_Locate_DevNode(out uint deviceNode,
            string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parentDeviceNode,
            uint deviceNode, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Child(out uint childDeviceNode,
            uint deviceNode, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Sibling(out uint siblingDeviceNode,
            uint deviceNode, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode,
            EntryPoint = "CM_Get_DevNode_PropertyW")]
        private static extern int CM_Get_DevNode_Property(uint deviceNode,
            in DevPropKey propertyKey, out uint propertyType,
            byte[] propertyBuffer, ref uint propertyBufferSize, uint flags);
    }
}
