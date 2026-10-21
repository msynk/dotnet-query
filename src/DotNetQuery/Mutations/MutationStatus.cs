namespace DotNetQuery;

/// <summary>Describes the lifecycle status of a mutation.</summary>
public enum MutationStatus
{
    /// <summary>The mutation has not been invoked.</summary>
    Idle,

    /// <summary>The mutation is currently running.</summary>
    Pending,

    /// <summary>The mutation completed successfully.</summary>
    Success,

    /// <summary>The mutation failed.</summary>
    Error,
}
