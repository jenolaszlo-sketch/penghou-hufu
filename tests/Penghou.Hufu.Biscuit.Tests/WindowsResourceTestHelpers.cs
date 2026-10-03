using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Penghou.Hufu.Biscuit.Tests;

/// <summary>Windows-only physical filesystem helpers for provider-boundary integration tests.</summary>
internal static class WindowsResourceTestHelpers
{
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const int FileAttributeTagInfoClass = 9;

    /// <summary>Creates a directory junction to an existing absolute target directory.</summary>
    internal static void CreateJunction(string junctionPath, string targetPath)
    {
        RequireWindows();
        var junction = GetFullAbsolutePath(junctionPath, nameof(junctionPath));
        var target = GetFullAbsolutePath(targetPath, nameof(targetPath));
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException("The junction target directory must already exist.");
        if (File.Exists(junction) || Directory.Exists(junction))
            throw new IOException("The junction path must not already exist.");
        var parent = Path.GetDirectoryName(junction);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("The junction parent directory must already exist.");
        EnsureNoReparseAncestors(parent);

        Directory.CreateDirectory(junction);
        try
        {
            var substituteName = ToSubstituteName(target);
            var printName = target;
            var substituteBytes = Encoding.Unicode.GetBytes(substituteName + "\0");
            var printBytes = Encoding.Unicode.GetBytes(printName + "\0");
            var pathBytes = checked(substituteBytes.Length + printBytes.Length);
            var dataLength = checked(8 + pathBytes);
            if (dataLength > ushort.MaxValue)
                throw new PathTooLongException("The junction target exceeds the Windows reparse buffer limit.");

            var buffer = new byte[8 + dataLength];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), IoReparseTagMountPoint);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)dataLength));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)(substituteBytes.Length - 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)substituteBytes.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)(printBytes.Length - 2)));
            substituteBytes.CopyTo(buffer, 16);
            printBytes.CopyTo(buffer, 16 + substituteBytes.Length);

            using var handle = CreateFileW(ToNativePath(junction), GenericWrite, ShareAll, IntPtr.Zero,
                OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open junction directory for reparse setup.");
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install test directory junction.");
        }
        catch
        {
            // Remove only the empty placeholder created above. Never recurse through a reparse point.
            if (Directory.Exists(junction) && !IsReparsePoint(junction))
                Directory.Delete(junction, recursive: false);
            throw;
        }
    }

    /// <summary>Deletes only a verified mount-point junction beneath the supplied workspace root.</summary>
    internal static void DeleteJunction(string junctionPath, string workspaceRoot)
    {
        RequireWindows();
        var root = GetFullAbsolutePath(workspaceRoot, nameof(workspaceRoot));
        var junction = GetFullAbsolutePath(junctionPath, nameof(junctionPath));
        var rootWithSeparator = EnsureTrailingSeparator(root);
        if (string.Equals(root, junction, StringComparison.OrdinalIgnoreCase) ||
            !junction.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The junction must be a child of the supplied workspace root.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The workspace root must exist.");
        EnsureNoReparseAncestors(root);

        var relativeParent = Path.GetDirectoryName(junction);
        if (relativeParent is null || !IsWithinOrEqual(root, relativeParent))
            throw new InvalidOperationException("The junction parent is outside the supplied workspace root.");
        EnsureNoReparseAncestors(relativeParent);
        if (!Directory.Exists(junction) || !IsMountPointJunction(junction))
            throw new InvalidOperationException("The target path is not a directory mount-point junction.");

        // Directory.Delete without recursion removes the reparse entry, never its target tree.
        Directory.Delete(junction, recursive: false);
    }

    /// <summary>Returns the operating system's absolute short-path spelling for an existing file or directory.</summary>
    internal static string GetShortPath(string physicalPath)
    {
        RequireWindows();
        var path = GetFullAbsolutePath(physicalPath, nameof(physicalPath));
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("The physical path must exist before its short name is queried.", path);

        var capacity = 260;
        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetShortPathNameW(path, buffer, checked((uint)capacity));
            if (length == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query the Windows short-path spelling.");
            if (length < capacity)
                return buffer.ToString();
            capacity = checked((int)length + 1);
        }
        throw new PathTooLongException("The Windows short-path result exceeded the supported buffer bound.");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("These physical filesystem test helpers require Windows.");
    }

    private static string GetFullAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("A fully qualified physical path is required.", parameterName);
        return Path.GetFullPath(path);
    }

    private static string ToSubstituteName(string fullPath) => fullPath.StartsWith("\\\\", StringComparison.Ordinal)
        ? @"\??\UNC\" + fullPath[2..]
        : @"\??\" + fullPath;

    private static string ToNativePath(string fullPath)
    {
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
    }

    private static bool IsWithinOrEqual(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(EnsureTrailingSeparator(root), StringComparison.OrdinalIgnoreCase);

    private static string EnsureTrailingSeparator(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0 || string.Equals(trimmed, volumeRoot, StringComparison.OrdinalIgnoreCase))
            return full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar)
                ? full
                : full + Path.DirectorySeparatorChar;
        return trimmed + Path.DirectorySeparatorChar;
    }

    private static void EnsureNoReparseAncestors(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("The physical path has no volume root.", nameof(path));
        var current = root;
        if (Directory.Exists(current) && IsReparsePoint(current))
            throw new InvalidOperationException("A path ancestor is a reparse point.");
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && IsReparsePoint(current))
                throw new InvalidOperationException("A path ancestor is a reparse point.");
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsMountPointJunction(string path)
    {
        using var handle = CreateFileW(ToNativePath(path), FileReadAttributes, ShareAll, IntPtr.Zero,
            OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to inspect the junction reparse tag.");
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var info, sizeof(uint) * 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query the junction reparse tag.");
        return (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 && info.ReparseTag == IoReparseTagMountPoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        internal uint Attributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte[] inputBuffer,
        int inputBufferSize, byte[]? outputBuffer, int outputBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out AttributeTagInfo info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);
}
