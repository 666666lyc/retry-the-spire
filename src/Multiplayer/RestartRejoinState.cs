namespace Retry;

internal sealed class RestartRejoinState
{
    internal bool _autoRejoinArmed;
    internal bool _suppressExpectedDisconnect;
    internal string? _oldLobbyIdentifier;
    internal AutoRejoinSession? _clientSession;
}
