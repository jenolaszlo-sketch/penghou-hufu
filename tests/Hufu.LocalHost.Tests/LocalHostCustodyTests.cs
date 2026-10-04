using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Hufu.LocalHost;

namespace Hufu.LocalHost.Tests;

public sealed class LocalHostCustodyTests
{
    [Fact]
    public void CreateNew_CreatesPrivateSeparatedRootsAndValidatesThem()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scope = NewScope();
        Directory.CreateDirectory(scope);
        var root = Path.Combine(scope, "host");
        try
        {
            var custody = LocalHostCustody.CreateNew(root, CurrentSid());

            Assert.Equal(Path.Combine(root, "control"), custody.ControlRoot);
            Assert.Equal(Path.Combine(root, "workspace"), custody.WorkspaceRoot);
            Assert.NotEqual(custody.ControlRoot, custody.WorkspaceRoot);
            Assert.True(Directory.Exists(custody.ControlRoot));
            Assert.True(Directory.Exists(custody.WorkspaceRoot));
            custody.Validate();

            var reopened = LocalHostCustody.OpenExisting(root, CurrentSid());
            Assert.Equal(custody.RootPath, reopened.RootPath);
            reopened.Validate();
        }
        finally
        {
            DeleteExactQualificationScope(scope);
        }
    }

    [Fact]
    public void CreateNew_RejectsExistingUnrelatedPathWithoutChangingIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scope = NewScope();
        Directory.CreateDirectory(scope);
        var root = Path.Combine(scope, "host");
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "keep.txt");
        File.WriteAllText(marker, "keep");
        try
        {
            const AccessControlSections ownerAndDacl = AccessControlSections.Owner | AccessControlSections.Access;
            var before = new DirectoryInfo(root).GetAccessControl(ownerAndDacl)
                .GetSecurityDescriptorSddlForm(ownerAndDacl);

            Assert.Throws<IOException>(() => LocalHostCustody.CreateNew(root, CurrentSid()));

            var after = new DirectoryInfo(root).GetAccessControl(ownerAndDacl)
                .GetSecurityDescriptorSddlForm(ownerAndDacl);
            Assert.Equal(before, after);
            Assert.Equal("keep", File.ReadAllText(marker));
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally
        {
            DeleteExactQualificationScope(scope);
        }
    }

    [Fact]
    public void Validate_RejectsUnexpectedAclOnControlFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scope = NewScope();
        Directory.CreateDirectory(scope);
        var root = Path.Combine(scope, "host");
        try
        {
            var custody = LocalHostCustody.CreateNew(root, CurrentSid());
            var db = Path.Combine(custody.ControlRoot, "journal.db");
            File.WriteAllText(db, "test database placeholder");
            var file = new FileInfo(db);
            var security = file.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read,
                AccessControlType.Allow));
            file.SetAccessControl(security);

            Assert.Throws<UnauthorizedAccessException>(custody.Validate);
        }
        finally
        {
            DeleteExactQualificationScope(scope);
        }
    }

    [Fact]
    public void ValidateControlFile_RejectsNamesOutsideControlDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scope = NewScope();
        Directory.CreateDirectory(scope);
        var root = Path.Combine(scope, "host");
        try
        {
            var custody = LocalHostCustody.CreateNew(root, CurrentSid());
            Assert.Throws<ArgumentException>(() => custody.ValidateControlFile("..\\workspace\\payload"));
            Assert.Throws<ArgumentException>(() => custody.ValidateControlFile(Path.GetFullPath("outside.db")));
        }
        finally
        {
            DeleteExactQualificationScope(scope);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_RejectsExcessiveNamespaceSizeOrDepth(bool excessiveDepth)
    {
        var scope = NewScope();
        try
        {
            var custody = LocalHostCustody.CreateNew(scope, CurrentSid());
            if (excessiveDepth)
            {
                var path = custody.WorkspaceRoot;
                for (var i = 0; i < 13; i++) path = Path.Combine(path, "nested");
                Directory.CreateDirectory(path);
            }
            else
                for (var i = 0; i < 257; i++) File.WriteAllText(Path.Combine(custody.WorkspaceRoot, $"file-{i}"), "x");
            Assert.Throws<UnauthorizedAccessException>(custody.Validate);
        }
        finally { DeleteExactQualificationScope(scope); }
    }

    [Fact]
    public void CreateNew_RejectsReparsePointInPathAncestors()
    {
        if (!OperatingSystem.IsWindows()) return;

        var tempScope = NewScope();
        var realParent = Path.Combine(tempScope, "real");
        var linkParent = Path.Combine(tempScope, "link");
        Directory.CreateDirectory(realParent);
        try
        {
            // Directory junctions do not require Developer Mode or elevation.
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("/d");
            process.StartInfo.ArgumentList.Add("/c");
            process.StartInfo.ArgumentList.Add("mklink");
            process.StartInfo.ArgumentList.Add("/J");
            process.StartInfo.ArgumentList.Add(linkParent);
            process.StartInfo.ArgumentList.Add(realParent);
            Assert.True(process.Start());
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);

            var nestedRoot = Path.Combine(linkParent, "new-host");
            Assert.Throws<UnauthorizedAccessException>(() => LocalHostCustody.CreateNew(nestedRoot, CurrentSid()));
            Assert.False(Directory.Exists(Path.Combine(realParent, "new-host")));
        }
        finally
        {
            // The cleanup target was generated below the exact user profile
            // by this test. Remove the junction itself before its target tree.
            if (Directory.Exists(linkParent)) Directory.Delete(linkParent);
            DeleteExactQualificationScope(tempScope);
        }
    }

    private static SecurityIdentifier CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("Current identity has no SID.");
    }

    private static string NewScope() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Hufu.LocalHost.Qualification-" + Guid.NewGuid().ToString("N"));

    private static void DeleteExactQualificationScope(string path)
    {
        var profileRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        var leaf = Path.GetFileName(fullPath);
        if (!fullPath.StartsWith(profileRoot, StringComparison.OrdinalIgnoreCase) ||
            !leaf.StartsWith("Hufu.LocalHost.Qualification-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(leaf["Hufu.LocalHost.Qualification-".Length..], "N", out _))
        {
            throw new InvalidOperationException("Refusing to clean outside the test's exact generated qualification scope.");
        }

        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        else if (File.Exists(fullPath)) File.Delete(fullPath);
    }
}
