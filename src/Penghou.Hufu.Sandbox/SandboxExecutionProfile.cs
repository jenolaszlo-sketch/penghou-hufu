using System.Collections.ObjectModel;
using Gagamba.Execution;

namespace Penghou.Hufu.Sandbox;

/// <summary>
/// One host-approved invocation: the only command shapes an activity may
/// request. It fixes the executable, arguments, working directory and
/// permitted environment names, and caps the execution guarantees an activity
/// may request. Registration is not permission — Hufu authority still decides
/// whether a given activity may execute the executable.
/// </summary>
public sealed record SandboxApprovedInvocation
{
    public SandboxApprovedInvocation(string id, string workspaceId, string executableRelativePath,
        string executable, string arguments, string workingDirectory,
        IReadOnlyList<string> allowedEnvironmentNames, IReadOnlyList<ExecutionRequirement> guaranteeCeiling)
    {
        if (!AuthorityValidation.ValidToken(id))
            throw new ArgumentException("A bounded invocation id is required.", nameof(id));
        if (!AuthorityValidation.ValidToken(workspaceId))
            throw new ArgumentException("A bounded workspace id is required.", nameof(workspaceId));
        if (string.IsNullOrEmpty(executableRelativePath) || !IsCanonical(executableRelativePath))
            throw new ArgumentException("A canonical relative executable path is required.", nameof(executableRelativePath));
        Bounds.Path(executable, nameof(executable));
        Bounds.Text(arguments, nameof(arguments));
        Bounds.Path(workingDirectory, nameof(workingDirectory));
        ArgumentNullException.ThrowIfNull(allowedEnvironmentNames);
        ArgumentNullException.ThrowIfNull(guaranteeCeiling);
        var names = allowedEnvironmentNames.ToArray();
        if (names.Any(name => !Bounds.EnvironmentName(name)) ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException("Invalid or duplicate environment name.", nameof(allowedEnvironmentNames));
        var ceiling = guaranteeCeiling.ToArray();
        if (ceiling.Any(requirement => !Enum.IsDefined(requirement.Capability) || !Enum.IsDefined(requirement.Minimum)))
            throw new ArgumentException("Invalid guarantee ceiling.", nameof(guaranteeCeiling));
        Id = id;
        WorkspaceId = workspaceId;
        ExecutableRelativePath = executableRelativePath;
        Executable = executable;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        AllowedEnvironmentNames = Array.AsReadOnly(names);
        GuaranteeCeiling = Array.AsReadOnly(ceiling);
    }

    public string Id { get; }
    public string WorkspaceId { get; }
    /// <summary>Canonical Hufu-relative path authorized as the ExecuteProcess resource.</summary>
    public string ExecutableRelativePath { get; }
    /// <summary>Absolute native executable path (host-resolved, trusted).</summary>
    public string Executable { get; }
    public string Arguments { get; }
    /// <summary>Absolute native working directory (host-resolved, trusted).</summary>
    public string WorkingDirectory { get; }
    public IReadOnlyList<string> AllowedEnvironmentNames { get; }
    public IReadOnlyList<ExecutionRequirement> GuaranteeCeiling { get; }

    private static bool IsCanonical(string path)
    {
        try { return AuthorityValidation.NormalizePath(path) == path; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>
/// The trusted host registry of approved invocations plus the platform-owned
/// environment that is always present. An activity can select an invocation
/// and supply only allowed environment values; it can never widen the shape.
/// </summary>
public sealed class SandboxExecutionProfile
{
    public SandboxExecutionProfile(string profileId, string profileRevision,
        IReadOnlyDictionary<string, string>? platformEnvironment,
        IEnumerable<SandboxApprovedInvocation> invocations)
    {
        if (!AuthorityValidation.ValidToken(profileId))
            throw new ArgumentException("A bounded profile id is required.", nameof(profileId));
        if (!AuthorityValidation.ValidToken(profileRevision))
            throw new ArgumentException("A bounded profile revision is required.", nameof(profileRevision));
        ArgumentNullException.ThrowIfNull(invocations);
        var registry = new Dictionary<string, SandboxApprovedInvocation>(StringComparer.Ordinal);
        var ordered = new List<SandboxApprovedInvocation>();
        foreach (var invocation in invocations)
        {
            if (invocation is null) throw new ArgumentException("A null invocation is not allowed.", nameof(invocations));
            if (!registry.TryAdd(invocation.Id, invocation))
                throw new ArgumentException($"Duplicate invocation id '{invocation.Id}'.", nameof(invocations));
            ordered.Add(invocation);
        }
        var environment = platformEnvironment is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(platformEnvironment, StringComparer.Ordinal);
        foreach (var entry in environment)
        {
            if (!Bounds.EnvironmentName(entry.Key) || entry.Value.Contains('\0'))
                throw new ArgumentException("Invalid platform environment entry.", nameof(platformEnvironment));
            if (ordered.Any(invocation => invocation.AllowedEnvironmentNames.Contains(entry.Key, StringComparer.Ordinal)))
                throw new ArgumentException($"Platform environment '{entry.Key}' collides with an allowed request name.", nameof(platformEnvironment));
        }
        ProfileId = profileId;
        ProfileRevision = profileRevision;
        PlatformEnvironment = new ReadOnlyDictionary<string, string>(environment);
        _registry = registry;
        Invocations = ordered.AsReadOnly();
    }

    /// <summary>Stable identity of this trusted execution policy.</summary>
    public string ProfileId { get; }
    /// <summary>Exact revision the launch is bound to; changes require a new request.</summary>
    public string ProfileRevision { get; }
    public IReadOnlyDictionary<string, string> PlatformEnvironment { get; }
    public IReadOnlyList<SandboxApprovedInvocation> Invocations { get; }

    public bool TryGetInvocation(string id, out SandboxApprovedInvocation? invocation) =>
        _registry.TryGetValue(id, out invocation);

    private readonly Dictionary<string, SandboxApprovedInvocation> _registry;
}

internal static class Bounds
{
    public static void Path(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\0'))
            throw new ArgumentException("A bounded path is required.", name);
    }

    public static void Text(string value, string name)
    {
        if (value is null || value.Length > 4096 || value.Contains('\0'))
            throw new ArgumentException("Bounded text is required.", name);
    }

    public static bool EnvironmentName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 256 && !name.Contains('=') &&
        !name.Contains('\0') && !name.Any(char.IsControl);
}
