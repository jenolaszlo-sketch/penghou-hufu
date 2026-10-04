using System.Security.AccessControl;
using System.Security.Principal;

namespace Hufu.LocalHost;

/// <summary>
/// Creates and checks a private NTFS namespace for the local host's control data
/// and its separately writable workspace. This is filesystem custody, not a
/// sandbox against privileged code running as the same Windows account.
/// </summary>
internal sealed class LocalHostCustody
{
    private static readonly SecurityIdentifier LocalSystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private readonly SecurityIdentifier _operatorSid;

    private LocalHostCustody(string rootPath, SecurityIdentifier operatorSid)
    {
        RootPath = rootPath;
        ControlRoot = Path.Combine(rootPath, "control");
        WorkspaceRoot = Path.Combine(rootPath, "workspace");
        _operatorSid = operatorSid;
    }

    internal string RootPath { get; }

    internal string ControlRoot { get; }

    internal string WorkspaceRoot { get; }

    /// <summary>
    /// Allocates a previously absent root beneath an existing, non-reparse
    /// parent. Existing user paths are never repaired or silently tightened.
    /// The caller must choose a dedicated host-owned parent directory.
    /// </summary>
    internal static LocalHostCustody CreateNew(string rootPath, SecurityIdentifier operatorSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(operatorSid);
        EnsureWindows();

        var currentSid = GetCurrentSid();
        if (!currentSid.Equals(operatorSid))
        {
            throw new UnauthorizedAccessException("The configured operator SID must match the current Windows identity.");
        }

        var fullRoot = NormalizeLocalDrivePath(rootPath);
        EnsureNtfs(fullRoot);
        RejectReparsePathAncestors(fullRoot, includeLeaf: false);
        ValidateAncestorsForCustody(fullRoot, operatorSid);

        if (Directory.Exists(fullRoot) || File.Exists(fullRoot))
        {
            throw new IOException("The host state root already exists; custody creation requires a new path.");
        }

        var parent = Path.GetDirectoryName(fullRoot);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("The parent of the host state root must already exist.");
        }

        var custody = new LocalHostCustody(fullRoot, operatorSid);
        System.IO.FileSystemAclExtensions.Create(new DirectoryInfo(fullRoot), CreateDirectorySecurity(operatorSid));
        custody.ValidateDirectory(fullRoot, requireProtectedDacl: true);

        CreateChildDirectory(custody.ControlRoot, operatorSid);
        CreateChildDirectory(custody.WorkspaceRoot, operatorSid);
        custody.Validate();
        return custody;
    }

    /// <summary>
    /// Reopens a previously provisioned host namespace without changing its
    /// ACLs or creating missing paths.
    /// </summary>
    internal static LocalHostCustody OpenExisting(string rootPath, SecurityIdentifier operatorSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(operatorSid);
        EnsureWindows();
        if (!GetCurrentSid().Equals(operatorSid))
        {
            throw new UnauthorizedAccessException("The configured operator SID must match the current Windows identity.");
        }

        var fullRoot = NormalizeLocalDrivePath(rootPath);
        EnsureNtfs(fullRoot);
        RejectReparsePathAncestors(fullRoot, includeLeaf: true);
        ValidateAncestorsForCustody(fullRoot, operatorSid);

        var custody = new LocalHostCustody(fullRoot, operatorSid);
        custody.Validate();
        return custody;
    }

    /// <summary>
    /// Rechecks identity, volume, reparse points, owners, and closed ACLs for
    /// every object under both roots. Call immediately before each host service
    /// operation. These checks do not make check/use races atomic against a
    /// privileged process or arbitrary process running as the operator.
    /// </summary>
    internal void Validate()
    {
        EnsureWindows();
        if (!GetCurrentSid().Equals(_operatorSid))
        {
            throw new UnauthorizedAccessException("The current Windows identity no longer matches the configured operator SID.");
        }

        var normalized = NormalizeLocalDrivePath(RootPath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(normalized, RootPath))
        {
            throw new IOException("The host state root path changed after custody creation.");
        }

        EnsureNtfs(RootPath);
        RejectReparsePathAncestors(RootPath, includeLeaf: true);
        ValidateAncestorsForCustody(RootPath, _operatorSid);
        ValidateDirectory(RootPath, requireProtectedDacl: true);
        ValidateDirectory(ControlRoot, requireProtectedDacl: false);
        ValidateDirectory(WorkspaceRoot, requireProtectedDacl: false);
        var remaining = 256;
        ValidateTree(RootPath, depth: 0, ref remaining);
    }

    /// <summary>
    /// Validates an existing direct child of the control directory, including
    /// database sidecars such as journal.db-wal and journal.db-shm, and returns
    /// its canonical path for the caller's next operation.
    /// </summary>
    internal string ValidateControlFile(string name)
    {
        Validate();
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            Path.IsPathRooted(name) || !StringComparer.Ordinal.Equals(Path.GetFileName(name), name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("A control file name must be one safe path component.", nameof(name));
        }

        var path = Path.Combine(ControlRoot, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The requested control file does not exist.", path);
        }

        ValidateFile(path);
        return path;
    }

    private void ValidateTree(string directory, int depth, ref int remaining)
    {
        if (depth > 12) throw new UnauthorizedAccessException("The local host namespace exceeds its depth bound.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (--remaining < 0) throw new UnauthorizedAccessException("The local host namespace exceeds its entry bound.");
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException($"Reparse points are not allowed in the host namespace: {entry}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                ValidateDirectory(entry, requireProtectedDacl: false);
                ValidateTree(entry, depth + 1, ref remaining);
            }
            else
            {
                ValidateFile(entry);
            }
        }
    }

    private void ValidateDirectory(string path, bool requireProtectedDacl)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            throw new DirectoryNotFoundException(path);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException($"Reparse points are not allowed in the host namespace: {path}");
        }

        var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        ValidateOwnerAndRules(security, path);
        if (requireProtectedDacl && !security.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException($"The host root must have a protected DACL: {path}");
        }
    }

    private void ValidateFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The control file disappeared during validation.", path);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException($"Reparse points are not allowed in the host namespace: {path}");
        }

        var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        ValidateOwnerAndRules(security, path);
    }

    private void ValidateOwnerAndRules(FileSystemSecurity security, string path)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        // Windows may assign Administrators as the default owner for native
        // SQLite/JSON files created by an elevated token. These principals
        // already have FullControl in this profile's closed DACL and are in its
        // trusted OS boundary; never accept an unrelated owner or repair ACLs.
        if (owner is null || !new[] { _operatorSid, LocalSystemSid, AdministratorsSid }.Contains(owner))
        {
            throw new UnauthorizedAccessException($"The host namespace object has an unexpected owner: {path}");
        }

        var expected = new HashSet<SecurityIdentifier> { _operatorSid, LocalSystemSid, AdministratorsSid };
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        if (rules.Count != expected.Count)
        {
            throw new UnauthorizedAccessException($"The host namespace object has an unexpected ACL: {path}");
        }

        foreach (AuthorizationRule rule in rules)
        {
            if (rule is not FileSystemAccessRule accessRule ||
                accessRule.AccessControlType != AccessControlType.Allow ||
                accessRule.FileSystemRights != FileSystemRights.FullControl ||
                accessRule.IdentityReference is not SecurityIdentifier sid ||
                !expected.Remove(sid))
            {
                throw new UnauthorizedAccessException($"The host namespace object has an unexpected ACL: {path}");
            }
        }

        if (expected.Count != 0)
        {
            throw new UnauthorizedAccessException($"The host namespace object is missing a required ACL entry: {path}");
        }
    }

    private static void CreateChildDirectory(string path, SecurityIdentifier operatorSid)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new IOException($"The new host namespace unexpectedly contains an existing child: {path}");
        }

        System.IO.FileSystemAclExtensions.Create(new DirectoryInfo(path), CreateDirectorySecurity(operatorSid));
    }

    private static DirectorySecurity CreateDirectorySecurity(SecurityIdentifier operatorSid)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(operatorSid);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { operatorSid, LocalSystemSid, AdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    private static string NormalizeLocalDrivePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal) ||
            fullPath.Length < 3 || !char.IsAsciiLetter(fullPath[0]) ||
            fullPath[1] != ':' || fullPath[2] != Path.DirectorySeparatorChar)
        {
            throw new ArgumentException("The host state root must be an absolute local drive path.", nameof(path));
        }

        if (StringComparer.OrdinalIgnoreCase.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), fullPath[..3].TrimEnd(Path.DirectorySeparatorChar)))
        {
            throw new ArgumentException("The host state root cannot be a drive root.", nameof(path));
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static void EnsureNtfs(string path)
    {
        var driveRoot = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(driveRoot) || !StringComparer.OrdinalIgnoreCase.Equals(new DriveInfo(driveRoot).DriveFormat, "NTFS"))
        {
            throw new PlatformNotSupportedException("Local host custody requires an NTFS volume.");
        }
    }

    private static void ValidateAncestorsForCustody(string path, SecurityIdentifier operatorSid)
    {
        var fullPath = Path.GetFullPath(path);
        var driveRoot = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("The path has no drive root.", nameof(path));
        var current = driveRoot;
        var trusted = new HashSet<SecurityIdentifier> { operatorSid, LocalSystemSid, AdministratorsSid };
        var segments = fullPath[driveRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        // Include the immediate parent but exclude the host root itself; the
        // host root is checked against its exact protected ACL separately.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            var info = new DirectoryInfo(current);
            if (!info.Exists)
            {
                continue;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException($"Reparse points are not allowed in host path ancestors: {current}");
            }

            var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner))
                throw new UnauthorizedAccessException($"An ancestor has an untrusted owner: {current}");
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
            foreach (AuthorizationRule rule in rules)
            {
                if (rule is not FileSystemAccessRule accessRule ||
                    accessRule.AccessControlType != AccessControlType.Allow ||
                    accessRule.IdentityReference is not SecurityIdentifier sid)
                {
                    // Fail closed on unfamiliar or deny ACEs: this helper does
                    // not attempt to calculate Windows effective access.
                    throw new UnauthorizedAccessException($"An ancestor has an ACL that cannot be proven safe: {current}");
                }

                if (!trusted.Contains(sid) && GrantsNamespaceMutation(accessRule.FileSystemRights))
                {
                    throw new UnauthorizedAccessException($"An untrusted principal can modify a host path ancestor: {current}");
                }
            }
        }
    }

    private static bool GrantsNamespaceMutation(FileSystemRights rights)
    {
        // For directories, WriteData/AppendData map to creating children;
        // those do not rewrite an existing protected entry. DeleteChild and
        // Delete can rename/remove an existing ancestor and are unsafe here.
        const FileSystemRights mutatingRights = FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.Delete |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;
        return (rights & mutatingRights) != 0;
    }

    private static void RejectReparsePathAncestors(string path, bool includeLeaf)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("The path has no drive root.", nameof(path));
        var current = root;
        var segments = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var count = includeLeaf ? segments.Length : segments.Length - 1;
        for (var i = 0; i < count; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (!Directory.Exists(current))
            {
                continue;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException($"Reparse points are not allowed in host path ancestors: {current}");
            }
        }
    }

    private static SecurityIdentifier GetCurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new UnauthorizedAccessException("The current Windows identity has no user SID.");
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Local host custody is supported only on Windows.");
        }
    }
}
