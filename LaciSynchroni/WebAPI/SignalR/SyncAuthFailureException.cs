namespace LaciSynchroni.WebAPI.SignalR;

public class SyncAuthFailureException : Exception
{
    public SyncAuthFailureException(string reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}