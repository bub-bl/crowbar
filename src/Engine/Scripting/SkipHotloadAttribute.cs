namespace Crowbar.Engine.Scripting;

/// <summary>
/// Opts a member (or an entire type) out of hot reload state migration.
/// Static field values marked with this attribute are not copied into the
/// reloaded assembly, and instance fields marked with it are not upgraded
/// when an object graph is walked. Apply it to a class/struct to skip the
/// type entirely.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class SkipHotloadAttribute : Attribute;
