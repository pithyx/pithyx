namespace Pithyx.Daemon.Realtime;

/// <summary>A message from the client: <c>sub</c> or <c>unsub</c> for a topic or pattern.</summary>
/// <param name="Type"><c>sub</c> or <c>unsub</c>.</param>
/// <param name="Id">Chosen by the client; echoed in the ack, snapshots and errors that answer this message.</param>
/// <param name="Topic">Topic name or pattern with <c>+</c> and <c>#</c>. A message without one is answered with an error.</param>
public sealed record ClientMessage(string Type, string Id, string? Topic = null);

/// <summary>A message from the server. <c>type</c> tells which of the other fields are set.</summary>
public abstract record ServerMessage(string Type, string RequestId);

/// <summary>Confirms a <c>sub</c> or <c>unsub</c>. For <c>sub</c>, the snapshots of matching topics follow.</summary>
public sealed record AckMessage(string Id, string RequestId) : ServerMessage("ack", RequestId);

/// <summary>The full current state of a topic, sent after <c>sub</c>. Only changes follow as events.</summary>
public sealed record SnapshotMessage(string Id, string Topic, object Data, string RequestId) : ServerMessage("snap", RequestId);

/// <summary>A change of a topic. <c>requestId</c> is the id of the action that caused it, if any.</summary>
public sealed record EventMessage(string Topic, object Data, string RequestId) : ServerMessage("event", RequestId);

/// <summary>A failed client message. <c>id</c> is empty when the message could not be parsed.</summary>
public sealed record ErrorMessage(string Id, string Code, IReadOnlyDictionary<string, object?> Params, string RequestId) : ServerMessage("error", RequestId);
