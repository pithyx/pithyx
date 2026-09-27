namespace Vyxis.Daemon.Realtime;

/// <summary>
/// A message from the client: <c>sub</c> or <c>unsub</c> for a topic or pattern, or <c>layout</c> to save the desktop
/// layout of this browser (debounced by the desktop).
/// </summary>
/// <param name="Type"><c>sub</c>, <c>unsub</c> or <c>layout</c>.</param>
/// <param name="Id">Chosen by the client; echoed in the ack, snapshots and errors that answer this message.</param>
/// <param name="Topic">Topic name or pattern with <c>+</c> and <c>#</c>; required for <c>sub</c> and <c>unsub</c>.</param>
/// <param name="Data">The layout for <c>layout</c>.</param>
public sealed record ClientMessage(string Type, string Id, string? Topic = null, System.Text.Json.JsonElement? Data = null);

/// <summary>A message from the server. <c>type</c> tells which of the other fields are set.</summary>
public abstract record ServerMessage(string Type, string RequestId);

/// <summary>Confirms a <c>sub</c>, <c>unsub</c> or <c>layout</c>. For <c>sub</c>, the snapshots of matching topics follow.</summary>
public sealed record AckMessage(string Id, string RequestId) : ServerMessage("ack", RequestId);

/// <summary>The full current state of a topic, sent after <c>sub</c>. Only changes follow as events.</summary>
public sealed record SnapshotMessage(string Id, string Topic, object Data, string RequestId) : ServerMessage("snap", RequestId);

/// <summary>A change of a topic. <c>requestId</c> is the id of the action that caused it, if any.</summary>
public sealed record EventMessage(string Topic, object Data, string RequestId) : ServerMessage("event", RequestId);

/// <summary>A failed client message. <c>id</c> is empty when the message could not be parsed.</summary>
public sealed record ErrorMessage(string Id, string Code, IReadOnlyDictionary<string, object?> Params, string RequestId) : ServerMessage("error", RequestId);
