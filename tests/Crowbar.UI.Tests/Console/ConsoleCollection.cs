namespace Crowbar.Console.Tests;

/// <summary>
/// Console tests share static state (Log.Entries, Log.EntryAdded, ConCmdRegistry
/// commands) and must run sequentially.
/// </summary>
[CollectionDefinition("Console")]
public sealed class ConsoleCollection : ICollectionFixture<ConsoleCollection>
{
}